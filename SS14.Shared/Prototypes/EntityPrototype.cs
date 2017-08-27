﻿using SS14.Shared.IoC;
using SS14.Shared.IoC.Exceptions;
using SS14.Shared.Interfaces.GameObjects;
using SS14.Shared.Interfaces.Reflection;
using SS14.Shared.Reflection;
using SS14.Shared.Prototypes;
using SS14.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YamlDotNet.RepresentationModel;
using SS14.Shared.Log;
using Vector2i = SS14.Shared.Maths.Vector2i;

namespace SS14.Shared.GameObjects
{
    /// <summary>
    /// Prototype that represents game entities.
    /// </summary>
    [Prototype("entity")]
    public class EntityPrototype : IPrototype, IIndexedPrototype, ISyncingPrototype
    {
        /// <summary>
        /// The "in code name" of the object. Must be unique.
        /// </summary>
        public string ID { get; private set; }

        /// <summary>
        /// The "in game name" of the object. What is displayed to most players.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// The type of entity instantiated when a new entity is created from this template.
        /// </summary>
        public Type ClassType { get; private set; }

        /// <summary>
        /// The different mounting points on walls. (If any).
        /// </summary>
        public List<int> MountingPoints { get; private set; }

        /// <summary>
        /// The Placement mode used for client-initiated placement. This is used for admin and editor placement. The serverside version controls what type the server assigns in normal gameplay.
        /// </summary>
        public string PlacementMode
        {
            get { return placementMode; }
            protected set { placementMode = value; }
        }
        private string placementMode = "PlaceNearby";

        /// <summary>
        /// The Range this entity can be placed from. This is only used serverside since the server handles normal gameplay. The client uses unlimited range since it handles things like admin spawning and editing.
        /// </summary>
        public int PlacementRange
        {
            get { return placementRange; }
            protected set { placementRange = value; }
        }
        private int placementRange = 200;

        /// <summary>
        /// Offset that is added to the position when placing. (if any). Client only.
        /// </summary>
        public Vector2i PlacementOffset { get; protected set; }

        /// <summary>
        /// The prototype we inherit from.
        /// </summary>
        public EntityPrototype Parent { get; private set; }

        /// <summary>
        /// A list of children inheriting from this prototype.
        /// </summary>
        public List<EntityPrototype> Children { get; private set; }
        public bool IsRoot => Parent == null;

        /// <summary>
        /// Used to store the parent id until we sync when all templates are done loading.
        /// </summary>
        private string parentTemp;

        /// <summary>
        /// A dictionary mapping the component type list to the YAML mapping containing their settings.
        /// </summary>
        public Dictionary<string, YamlMappingNode> Components { get; private set; } = new Dictionary<string, YamlMappingNode>();

        /// <summary>
        /// Bitflag to hold snapping categories that this object has applied to it such as pipe/wire/wallmount
        /// </summary>
        public SnapFlags SnapFlags { get; set; }

        /// <summary>
        /// The mapping node inside the <c>data</c> field of the prototype. Null if no data field exists.
        /// </summary>
        public YamlMappingNode DataNode;

        public void LoadFrom(YamlMappingNode mapping)
        {
            ID = mapping.GetNode("id").AsString();

            YamlNode node;
            if (mapping.TryGetNode("name", out node))
            {
                Name = node.AsString();
            }

            if (mapping.TryGetNode("class", out node))
            {
                var manager = IoCManager.Resolve<IReflectionManager>();
                ClassType = manager.GetType(node.AsString());
                // TODO: logging of when the ClassType doesn't exist: Safety for typos.
            }

            if (mapping.TryGetNode("parent", out node))
            {
                parentTemp = node.AsString();
            }

            // COMPONENTS
            if (mapping.TryGetNode<YamlSequenceNode>("components", out var sequence))
            {
                foreach (var componentMapping in sequence.Cast<YamlMappingNode>())
                {
                    ReadComponent(componentMapping);
                }
            }

            // DATA FIELD
            if (mapping.TryGetNode<YamlMappingNode>("data", out var dataMapping))
            {
                DataNode = dataMapping;
            }

            // PLACEMENT
            // TODO: move to a component or something. Shouldn't be a root part of prototypes IMO.
            if (mapping.TryGetNode<YamlMappingNode>("placement", out var placementMapping))
            {
                ReadPlacementProperties(placementMapping);
            }

            // Reads snapping flags that this object holds that describe its properties to such as wire/pipe/wallmount, used to prevent certain stacked placement
            if (mapping.TryGetNode<YamlMappingNode>("snap", out var snappingcategories))
            {
                foreach (var snapnode in sequence.Cast<YamlMappingNode>())
                {
                    SnapFlags |= snapnode.AsEnum<SnapFlags>();
                }
            }
        }

        private void ReadPlacementProperties(YamlMappingNode mapping)
        {
            YamlNode node;
            if (mapping.TryGetNode("mode", out node))
            {
                PlacementMode = node.AsString();
            }

            if (mapping.TryGetNode("offset", out node))
            {
                PlacementOffset = node.AsVector2i();
            }

            if (mapping.TryGetNode<YamlSequenceNode>("nodes", out var sequence))
            {
                MountingPoints = sequence.Select(p => p.AsInt()).ToList();
            }

            if (mapping.TryGetNode("range", out node))
            {
                PlacementRange = node.AsInt();
            }
        }

        // Resolve inheritance.
        public bool Sync(IPrototypeManager manager, int stage)
        {
            switch (stage)
            {
                case 0:
                    if (parentTemp == null)
                    {
                        return true;
                    }

                    Parent = manager.Index<EntityPrototype>(parentTemp);
                    if (Parent.Children == null)
                    {
                        Parent.Children = new List<EntityPrototype>();
                    }
                    Parent.Children.Add(this);
                    return false;

                case 1:
                    // We are a root-level prototype.
                    // As such we're getting the duty of pushing inheritance into everybody's face.
                    // Can't do a "puller" system where each queries the parent because it requires n stages
                    //  (n being the depth of each inheritance tree)

                    if (Children == null)
                    {
                        break;
                    }
                    foreach (EntityPrototype child in Children)
                    {
                        PushInheritance(this, child);
                    }

                    break;
            }
            return false;
        }

        private static void PushInheritance(EntityPrototype source, EntityPrototype target)
        {
            foreach (KeyValuePair<string, YamlMappingNode> component in source.Components)
            {
                if (target.Components.TryGetValue(component.Key, out YamlMappingNode targetComponent))
                {
                    // Copy over values the target component does not have.
                    foreach (YamlNode key in component.Value.Children.Keys)
                    {
                        if (!targetComponent.Children.ContainsKey(key))
                        {
                            targetComponent.Children[key] = component.Value[key];
                        }
                    }
                }
                else
                {
                    // Copy component into the target, since it doesn't have it yet.
                    target.Components[component.Key] = new YamlMappingNode(component.Value.AsEnumerable());
                }
            }

            if (target.Name == null)
            {
                target.Name = source.Name;
            }

            if (target.ClassType == null)
            {
                target.ClassType = source.ClassType;
            }

            if (target.Children == null)
            {
                return;
            }

            // TODO: remove recursion somehow.
            foreach (EntityPrototype child in target.Children)
            {
                PushInheritance(target, child);
            }
        }

        /// <summary>
        /// Creates an entity from this prototype.
        /// Do not call this directly, use the server entity manager instead.
        /// </summary>
        /// <returns></returns>
        public IEntity CreateEntity(int uid, IEntityManager manager, IEntityNetworkManager networkManager, IComponentFactory componentFactory)
        {
            var entity = (IEntity)Activator.CreateInstance(ClassType ?? typeof(Entity));

            entity.SetManagers(manager, networkManager);
            entity.SetUid(uid);
            entity.Name = Name;
            entity.Prototype = this;

            foreach (KeyValuePair<string, YamlMappingNode> componentData in Components)
            {
                IComponent component = componentFactory.GetComponent(componentData.Key);
                component.LoadParameters(componentData.Value);
                entity.AddComponent(component);
            }

            if (DataNode != null)
            {
                entity.LoadData(DataNode);
            }

            return entity;
        }

        // 100% completely refined & distilled cancer.
        public IEnumerable<ComponentParameter> GetBaseSpriteParamaters()
        {
            // Emotional programming.
            if (Components.TryGetValue("Icon", out YamlMappingNode ಠ_ಠ))
            {
                if (ಠ_ಠ.TryGetNode("icon", out YamlNode ಥ_ಥ))
                {
                    return new ComponentParameter[] { new ComponentParameter("icon", ಥ_ಥ.AsString()) };
                }
            }
            return new ComponentParameter[0];
        }

        private void ReadComponent(YamlMappingNode mapping)
        {
            var factory = IoCManager.Resolve<IComponentFactory>();
            string type = mapping.GetNode("type").AsString();
            // See if type exists to detect errors.
            switch (factory.GetComponentAvailability(type))
            {
                case ComponentAvailability.Available:
                    break;

                case ComponentAvailability.Ignore:
                    return;

                case ComponentAvailability.Unknown:
                    Log.Logger.Error($"Unknown component '{type}' in prototype {ID}!");
                    return;
            }

            var copy = new YamlMappingNode(mapping.AsEnumerable());
            // TODO: figure out a better way to exclude the type node.
            // Also maybe deep copy this? Right now it's pretty error prone.
            copy.Children.Remove(new YamlScalarNode("type"));

            Components[type] = copy;
        }

        public override string ToString()
        {
            return $"EntityPrototype({ID})";
        }
    }
}
