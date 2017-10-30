using System;
using System.Collections.Generic;
using Entitas.Utils;
using QFramework;

namespace Entitas
{
    /// A context manages the lifecycle of entities and groups.
    /// You can create and destroy entities and get groups of entities.
    /// The prefered way to create a context is to use the generated methods
    /// from the code generator, e.g. var context = new GameContext();
    public class Context<TEntity> : IContext<TEntity> where TEntity : class, IEntity
    {

        /// Occurs when an entity gets created.
        public event ContextEntityChanged OnEntityCreated;

        /// Occurs when an entity will be destroyed.
        public event ContextEntityChanged OnEntityWillBeDestroyed;

        /// Occurs when an entity got destroyed.
        public event ContextEntityChanged OnEntityDestroyed;

        /// Occurs when a group gets created for the first time.
        public event ContextGroupChanged OnGroupCreated;

        /// The total amount of components an entity can possibly have.
        /// This value is generated by the code generator,
        /// e.g ComponentLookup.TotalComponents.
        public int TotalComponents
        {
            get { return mTotalComponents; }
        }

        /// Returns all componentPools. componentPools is used to reuse
        /// removed components.
        /// Removed components will be pushed to the componentPool.
        /// Use entity.CreateComponent(index, type) to get a new or reusable
        /// component from the componentPool.
        public Stack<IComponent>[] ComponentPools
        {
            get { return mComponentPools; }
        }

        /// The contextInfo contains information about the context.
        /// It's used to provide better error messages.
        public ContextInfo ContextInfo
        {
            get { return _contextInfo; }
        }

        /// Returns the number of entities in the context.
        public int Count
        {
            get { return _entities.Count; }
        }

        /// Returns the number of entities in the internal ECSObjectPool
        /// for entities which can be reused.
        public int ReusableEntitiesCount
        {
            get { return _reusableEntities.Count; }
        }

        /// Returns the number of entities that are currently retained by
        /// other objects (e.g. Group, Collector, ReactiveSystem).
        public int RetainedEntitiesCount
        {
            get { return _retainedEntities.Count; }
        }

        readonly int mTotalComponents;

        readonly Stack<IComponent>[] mComponentPools;
        readonly ContextInfo _contextInfo;
        readonly Func<IEntity, IRefCounter> _aercFactory;

        readonly HashSet<TEntity> _entities = new HashSet<TEntity>(EntityEqualityComparer<TEntity>.Comparer);
        readonly Stack<TEntity> _reusableEntities = new Stack<TEntity>();
        readonly HashSet<TEntity> _retainedEntities = new HashSet<TEntity>(EntityEqualityComparer<TEntity>.Comparer);

        readonly Dictionary<IMatcher<TEntity>, IGroup<TEntity>> _groups =
            new Dictionary<IMatcher<TEntity>, IGroup<TEntity>>();

        readonly List<IGroup<TEntity>>[] _groupsForIndex;
        readonly SimpleObjectPool<List<GroupChanged<TEntity>>> mGroupChangedListSimpleObjectPool;

        readonly Dictionary<string, IEntityIndex> _entityIndices;

        int _creationIndex;

        TEntity[] _entitiesCache;

        // Cache delegates to avoid gc allocations
        EntityComponentChanged _cachedEntityChanged;

        EntityComponentReplaced _cachedComponentReplaced;
        EntityEvent _cachedEntityReleased;
        EntityEvent _cachedDestroyEntity;

        /// The prefered way to create a context is to use the generated methods
        /// from the code generator, e.g. var context = new GameContext();
        public Context(int totalComponents) : this(totalComponents, 0, null, null)
        {
        }

        // TODO Obsolete since 0.41.0, April 2017
        [Obsolete(
            "Migration Support for 0.41.0. Please use new Context(totalComponents, startCreationIndex, contextInfo, aercFactory)")]
        public Context(int totalComponents, int startCreationIndex, ContextInfo contextInfo)
            : this(totalComponents,
                startCreationIndex,
                contextInfo,
                (entity) => new SafeARC(entity))
        {
        }

        /// The prefered way to create a context is to use the generated methods
        /// from the code generator, e.g. var context = new GameContext();
        public Context(int totalComponents, int startCreationIndex, ContextInfo contextInfo,
            Func<IEntity, IRefCounter> aercFactory)
        {
            mTotalComponents = totalComponents;
            _creationIndex = startCreationIndex;

            if (contextInfo != null)
            {
                _contextInfo = contextInfo;
                if (contextInfo.ComponentNames.Length != totalComponents)
                {
                    throw new ContextInfoException(this, contextInfo);
                }
            }
            else
            {
                _contextInfo = createDefaultContextInfo();
            }

            _aercFactory = aercFactory == null
                ? (entity) => new SafeARC(entity)
                : aercFactory;

            _groupsForIndex = new List<IGroup<TEntity>>[totalComponents];
            mComponentPools = new Stack<IComponent>[totalComponents];
            _entityIndices = new Dictionary<string, IEntityIndex>();
            mGroupChangedListSimpleObjectPool = new SimpleObjectPool<List<GroupChanged<TEntity>>>(
                () => new List<GroupChanged<TEntity>>(),
                list => list.Clear()
            );

            // Cache delegates to avoid gc allocations
            _cachedEntityChanged = updateGroupsComponentAddedOrRemoved;
            _cachedComponentReplaced = updateGroupsComponentReplaced;
            _cachedEntityReleased = onEntityReleased;
            _cachedDestroyEntity = onDestroyEntity;
        }

        ContextInfo createDefaultContextInfo()
        {
            var componentNames = new string[mTotalComponents];
            const string prefix = "Index ";
            for (int i = 0; i < componentNames.Length; i++)
            {
                componentNames[i] = prefix + i;
            }

            return new ContextInfo("Unnamed Context", componentNames, null);
        }

        /// Creates a new entity or gets a reusable entity from the
        /// internal ECSObjectPool for entities.
        public TEntity CreateEntity()
        {
            TEntity entity;

            if (_reusableEntities.Count > 0)
            {
                entity = _reusableEntities.Pop();
                entity.Reactivate(_creationIndex++);
            }
            else
            {
                entity = (TEntity) Activator.CreateInstance(typeof(TEntity));
                entity.Initialize(_creationIndex++, mTotalComponents, mComponentPools, _contextInfo,
                    _aercFactory(entity));
            }

            _entities.Add(entity);
            entity.Retain(this);
            _entitiesCache = null;
            entity.OnComponentAdded += _cachedEntityChanged;
            entity.OnComponentRemoved += _cachedEntityChanged;
            entity.OnComponentReplaced += _cachedComponentReplaced;
            entity.OnEntityReleased += _cachedEntityReleased;
            entity.OnDestroyEntity += _cachedDestroyEntity;

            if (OnEntityCreated != null)
            {
                OnEntityCreated(this, entity);
            }

            return entity;
        }

        /// Destroys the entity, removes all its components and pushs it back
        /// to the internal ECSObjectPool for entities.
        // TODO Obsolete since 0.42.0, April 2017
        [Obsolete("Please use entity.Destroy()")]
        public void DestroyEntity(TEntity entity)
        {
            var removed = _entities.Remove(entity);
            if (!removed)
            {
                throw new ContextDoesNotContainEntityException(
                    "'" + this + "' cannot destroy " + entity + "!",
                    "This cannot happen!?!"
                );
            }
            _entitiesCache = null;

            if (OnEntityWillBeDestroyed != null)
            {
                OnEntityWillBeDestroyed(this, entity);
            }

            entity.InternalDestroy();

            if (OnEntityDestroyed != null)
            {
                OnEntityDestroyed(this, entity);
            }

            if (entity.RefCount == 1)
            {
                // Can be released immediately without
                // adding to _retainedEntities
                entity.OnEntityReleased -= _cachedEntityReleased;
                _reusableEntities.Push(entity);
                entity.Release(this);
                entity.RemoveAllOnEntityReleasedHandlers();
            }
            else
            {
                _retainedEntities.Add(entity);
                entity.Release(this);
            }
        }

        /// Destroys all entities in the context.
        /// Throws an exception if there are still retained entities.
        public void DestroyAllEntities()
        {
            var entities = GetEntities();
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i].Destroy();
            }

            _entities.Clear();

            if (_retainedEntities.Count != 0)
            {
                throw new ContextStillHasRetainedEntitiesException(this);
            }
        }

        /// Determines whether the context has the specified entity.
        public bool HasEntity(TEntity entity)
        {
            return _entities.Contains(entity);
        }

        /// Returns all entities which are currently in the context.
        public TEntity[] GetEntities()
        {
            if (_entitiesCache == null)
            {
                _entitiesCache = new TEntity[_entities.Count];
                _entities.CopyTo(_entitiesCache);
            }

            return _entitiesCache;
        }

        /// Returns a group for the specified matcher.
        /// Calling context.GetGroup(matcher) with the same matcher will always
        /// return the same instance of the group.
        public IGroup<TEntity> GetGroup(IMatcher<TEntity> matcher)
        {
            IGroup<TEntity> group;
            if (!_groups.TryGetValue(matcher, out group))
            {
                group = new Group<TEntity>(matcher);
                var entities = GetEntities();
                for (int i = 0; i < entities.Length; i++)
                {
                    group.HandleEntitySilently(entities[i]);
                }
                _groups.Add(matcher, group);

                for (int i = 0; i < matcher.indices.Length; i++)
                {
                    var index = matcher.indices[i];
                    if (_groupsForIndex[index] == null)
                    {
                        _groupsForIndex[index] = new List<IGroup<TEntity>>();
                    }
                    _groupsForIndex[index].Add(group);
                }

                if (OnGroupCreated != null)
                {
                    OnGroupCreated(this, group);
                }
            }

            return group;
        }

        /// Adds the IEntityIndex for the specified name.
        /// There can only be one IEntityIndex per name.
        public void AddEntityIndex(IEntityIndex entityIndex)
        {
            if (_entityIndices.ContainsKey(entityIndex.Name))
            {
                throw new ContextEntityIndexDoesAlreadyExistException(this, entityIndex.Name);
            }

            _entityIndices.Add(entityIndex.Name, entityIndex);
        }

        /// Gets the IEntityIndex for the specified name.
        public IEntityIndex GetEntityIndex(string name)
        {
            IEntityIndex entityIndex;
            if (!_entityIndices.TryGetValue(name, out entityIndex))
            {
                throw new ContextEntityIndexDoesNotExistException(this, name);
            }

            return entityIndex;
        }

        /// Resets the creationIndex back to 0.
        public void ResetCreationIndex()
        {
            _creationIndex = 0;
        }

        /// Clears the componentPool at the specified index.
        public void ClearComponentPool(int index)
        {
            var componentPool = mComponentPools[index];
            if (componentPool != null)
            {
                componentPool.Clear();
            }
        }

        /// Clears all componentPools.
        public void ClearComponentPools()
        {
            for (int i = 0; i < mComponentPools.Length; i++)
            {
                ClearComponentPool(i);
            }
        }

        /// Resets the context (destroys all entities and
        /// resets creationIndex back to 0).
        public void Reset()
        {
            DestroyAllEntities();
            ResetCreationIndex();

            OnEntityCreated = null;
            OnEntityWillBeDestroyed = null;
            OnEntityDestroyed = null;
            OnGroupCreated = null;
        }

        public override string ToString()
        {
            return _contextInfo.Name;
        }

        void updateGroupsComponentAddedOrRemoved(IEntity entity, int index, IComponent component)
        {
            var groups = _groupsForIndex[index];
            if (groups != null)
            {
                var events = mGroupChangedListSimpleObjectPool.Allocate();

                var tEntity = (TEntity) entity;

                for (int i = 0; i < groups.Count; i++)
                {
                    events.Add(groups[i].HandleEntity(tEntity));
                }

                for (int i = 0; i < events.Count; i++)
                {
                    var groupChangedEvent = events[i];
                    if (groupChangedEvent != null)
                    {
                        groupChangedEvent(
                            groups[i], tEntity, index, component
                        );
                    }
                }

                mGroupChangedListSimpleObjectPool.Recycle(events);
            }
        }

        void updateGroupsComponentReplaced(IEntity entity, int index, IComponent previousComponent,
            IComponent newComponent)
        {
            var groups = _groupsForIndex[index];
            if (groups != null)
            {

                var tEntity = (TEntity) entity;

                for (int i = 0; i < groups.Count; i++)
                {
                    groups[i].UpdateEntity(
                        tEntity, index, previousComponent, newComponent
                    );
                }
            }
        }

        void onEntityReleased(IEntity entity)
        {
            if (entity.IsEnabled)
            {
                throw new EntityIsNotDestroyedException(
                    "Cannot release " + entity + "!"
                );
            }
            var tEntity = (TEntity) entity;
            entity.RemoveAllOnEntityReleasedHandlers();
            _retainedEntities.Remove(tEntity);
            _reusableEntities.Push(tEntity);
        }

        void onDestroyEntity(IEntity entity)
        {
            DestroyEntity((TEntity) entity);
        }
    }
}