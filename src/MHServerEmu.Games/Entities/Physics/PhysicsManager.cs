﻿using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Generators;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Physics
{
    public class PhysicsManager
    {
        public int CurrentForceReadIndex => _currentForceReadWriteState ? 1 : 0;
        public int CurrentForceWriteIndex => _currentForceReadWriteState ? 0 : 1;

        private Game _game { get; }
        private List<ForceSystem> _pendingForceSystems { get; }
        private List<ForceSystem> _activeForceSystems { get; }
        private Queue<OverlapEvent> _overlapEvents { get; }
        private List<ulong> _entitiesPendingResolve { get; }
        private List<ulong> _entitiesResolving { get; }
        private int _physicsFrames;
        private bool _currentForceReadWriteState;

        public PhysicsManager(Game game)
        {
            _game = game;
            _pendingForceSystems = new();
            _activeForceSystems = new();
            _overlapEvents = new();
            _entitiesPendingResolve = new();
            _entitiesResolving = new();
            _currentForceReadWriteState = false;
            _physicsFrames = 1;
        }

        public void ResolveEntities()
        {
            if (_game == null || _entitiesResolving.Count > 0) return;

            _entitiesResolving.Clear();
            _entitiesResolving.AddRange(_entitiesPendingResolve);
            _entitiesPendingResolve.Clear();
            _physicsFrames++;

            SwapCurrentForceReadWriteIndices();
            ApplyForceSystems();
            PhysicsContext physicsContext = new();
            ResolveEntitiesAllowPenetration(physicsContext, _entitiesResolving);
            ResolveEntitiesOverlapState(physicsContext);

            _entitiesResolving.Clear();

            foreach (Region region in _game.RegionIterator())
                region.ClearCollidedEntities();
        }

        private void ResolveEntitiesOverlapState(PhysicsContext physicsContext)
        {
            foreach (var entityId in _entitiesResolving)
                ResolveEntitiesOverlapState(_game.EntityManager.GetEntity<WorldEntity>(entityId), _overlapEvents);

            foreach (var worldEntity in physicsContext.AttachedEntities)
                ResolveEntitiesOverlapState(worldEntity, _overlapEvents);

            while (_overlapEvents.Count > 0)
            {
                OverlapEvent overlapEvent = _overlapEvents.Dequeue();
                ResolveOverlapEvent(overlapEvent.Type, overlapEvent.Who, overlapEvent.Whom, overlapEvent.WhoPos, overlapEvent.WhomPos);
                ResolveOverlapEvent(overlapEvent.Type, overlapEvent.Whom, overlapEvent.Who, overlapEvent.WhomPos, overlapEvent.WhoPos);
            }
        }

        private void ResolveEntitiesOverlapState(WorldEntity worldEntity, Queue<OverlapEvent> overlapEvents)
        {
            if (worldEntity != null && worldEntity.IsInWorld)
            {
                var entityPhysics = worldEntity.Physics;
                var overlappedEntries = entityPhysics.OverlappedEntities.ToList();
                foreach (var overlappedEntry in overlappedEntries)
                    if (overlappedEntry.Value.Frame != _physicsFrames)
                    {
                        var overlappedEntity = _game.EntityManager.GetEntity<WorldEntity>(overlappedEntry.Key);
                        if (overlappedEntity != null)
                            overlapEvents.Enqueue(new (OverlapEventType.Remove, worldEntity, overlappedEntity));
                        else
                            entityPhysics.OverlappedEntities.Remove(overlappedEntry.Key);
                    }
            }
        }

        private void ResolveEntitiesAllowPenetration(PhysicsContext physicsContext, List<ulong> entitiesResolving)
        {
            if (_game == null) return;
            RegionManager regionManager = _game.RegionManager;
            if (regionManager == null) return;

            foreach (var entityId in entitiesResolving)
            {
                var worldEntity = _game.EntityManager.GetEntity<WorldEntity>(entityId);
                if (worldEntity == null || worldEntity.TestStatus(EntityStatus.Destroyed) || !worldEntity.IsInWorld)  continue;

                var entityPhysics = worldEntity.Physics;
                Vector3 externalForces = entityPhysics.GetExternalForces();
                Vector3 repulsionForces = entityPhysics.GetRepulsionForces();

                MoveEntityFlags moveFlags = 0;
                if (entityPhysics.HasExternalForces())
                    moveFlags |= MoveEntityFlags.SendToOwner | MoveEntityFlags.SendToClients;
                if (worldEntity.IsMovementAuthoritative)
                    moveFlags |= MoveEntityFlags.SendToOwner;

                if (Vector3.IsNearZero(repulsionForces, 0.5f) == false)
                {
                    float repulsionForcesLength = Vector3.Length(repulsionForces);
                    float collideRadius = worldEntity.EntityCollideBounds.Radius;
                    if (repulsionForcesLength > collideRadius)
                        repulsionForces *= (collideRadius / repulsionForcesLength);
                    externalForces += repulsionForces;
                }

                moveFlags |= MoveEntityFlags.SweepCollide | MoveEntityFlags.Sliding;
                MoveEntity(worldEntity, externalForces, moveFlags);

                entityPhysics.OnPhysicsUpdateFinished();
                UpdateAttachedEntityPositions(physicsContext, worldEntity);
            }
        }

        private void UpdateAttachedEntityPositions(PhysicsContext physicsContext, WorldEntity parentEntity)
        {
            if (parentEntity == null) return;
            if (parentEntity.Physics.GetAttachedEntities(out List<ulong> attachedEntities))
            {
                Vector3 parentEntityPosition = parentEntity.RegionLocation.Position;
                Orientation parentEntityOrientation = parentEntity.Orientation;

                foreach (var attachedEntityId in attachedEntities)
                {
                    var attachedEntity = _game.EntityManager.GetEntity<WorldEntity>(attachedEntityId);
                    if (attachedEntity != null && attachedEntity.IsInWorld)
                    {
                        var worldEntityProto = attachedEntity.WorldEntityPrototype;
                        if (worldEntityProto != null)
                        {
                            attachedEntity.ChangeRegionPosition(
                                parentEntityPosition, 
                                worldEntityProto.UpdateOrientationWithParent ? parentEntityOrientation : null, 
                                ChangePositionFlags.PhysicsResolve);
                            CheckForExistingCollisions(attachedEntity, false);
                            physicsContext.AttachedEntities.Add(attachedEntity);
                        }
                    }
                }
            }
        }

        private void ApplyForceSystems()
        {
            _activeForceSystems.AddRange(_pendingForceSystems);
            _pendingForceSystems.Clear();

            for (int i = _activeForceSystems.Count - 1; i >= 0; i--)
                if (ApplyForceSystemCheckCompletion(_activeForceSystems[i]))
                    _activeForceSystems.RemoveAt(i);
        }

        private bool ApplyForceSystemCheckCompletion(ForceSystem forceSystem)
        {
            bool complete = true;

            foreach (var member in forceSystem.Members.Iterate())
            {
                if (member == null) continue;
                bool active = false;

                WorldEntity entity = _game.EntityManager.GetEntity<WorldEntity>(member.EntityId);
                if (entity != null && entity.IsInWorld)
                    if (entity.TestStatus(EntityStatus.Destroyed))
                    {
                        float time = Math.Min((float)_game.FixedTimeBetweenUpdates.TotalSeconds, member.Time);
                        float distance = member.Speed * time + member.Acceleration * time * time / 2;
                        Vector3 vector = member.Direction * distance;
                        bool moved = MoveEntity(entity, vector, MoveEntityFlags.SendToOwner | MoveEntityFlags.SendToClients | MoveEntityFlags.SweepCollide);

                        bool collision = Vector3.LengthSquared(member.Position + vector - entity.RegionLocation.Position) > 0.01f;

                        member.Position = entity.RegionLocation.Position;
                        member.Time -= time;
                        member.Speed += member.Acceleration * time;

                        active = collision == false && Segment.IsNearZero(member.Time) == false;
                        complete &= !active;

                        if (moved) entity.UpdateNavigationInfluence();
                    }

                if (active == false)
                    forceSystem.Members.Remove(member);
            }

            return complete;
        }

        private bool MoveEntity(WorldEntity entity, Vector3 vector, MoveEntityFlags moveFlags)
        {
            if (_game == null || entity == null || entity.IsInWorld == false || entity.TestStatus(EntityStatus.Destroyed))
                return false;

            List<EntityCollision> entityCollisionList = new();
            bool moved = false;

            if (Vector3.IsNearZero(vector))
                CheckForExistingCollisions(entity, true);
            else
            {
                var locomotor = entity.Locomotor;
                if (locomotor == null)  return false;

                bool noMissile = locomotor.IsMissile == false;
                bool sliding = noMissile && moveFlags.HasFlag(MoveEntityFlags.Sliding);
                bool sweepCollide = moveFlags.HasFlag(MoveEntityFlags.SweepCollide);
                bool sendToOwner = moveFlags.HasFlag(MoveEntityFlags.SendToOwner);
                bool sendToClients = moveFlags.HasFlag(MoveEntityFlags.SendToClients);
                bool allowSweep = noMissile;

                Vector3 desiredDestination = new();
                if (GetDesiredDestination(entity, vector, allowSweep, ref desiredDestination, out bool clipped))
                {
                    Vector3 collidedDestination = Vector3.Zero;
                    if (sweepCollide)
                        moved = SweepEntityCollideToDestination(entity, desiredDestination, sliding, ref collidedDestination, entityCollisionList);
                    else
                    {
                        collidedDestination = desiredDestination;
                        moved = true;
                    }

                    if (moved)
                    {
                        locomotor.MovementImpeded = clipped || !Vector3.EpsilonSphereTest(collidedDestination, desiredDestination);

                        ChangePositionFlags changeFlags = ChangePositionFlags.PhysicsResolve;
                        changeFlags |= !sendToOwner ? ChangePositionFlags.NoSendToOwner : 0;
                        changeFlags |= !sendToClients ? ChangePositionFlags.NoSendToClients : 0;

                        entity.ChangeRegionPosition(collidedDestination, null, changeFlags);
                    }

                    if (sweepCollide)
                        HandleEntityCollisions(entityCollisionList, entity, true);

                    if (clipped && entity.TestStatus(EntityStatus.Destroyed) == false && entity.IsInWorld)
                        NotifyEntityCollision(entity, null, collidedDestination);
                }
            }

            return moved;
        }

        private bool SweepEntityCollideToDestination(WorldEntity entity, Vector3 desiredDestination, bool sliding, ref Vector3 collidedDestination, List<EntityCollision> entityCollisionList)
        {
            if (entity == null || entity.Region == null) return false;

            var location = entity.RegionLocation;
            Vector3 velocity = desiredDestination - location.Position;

            Aabb collideBounds = entity.EntityCollideBounds.ToAabb();
            collideBounds += collideBounds.Translate(velocity);

            SweepEntityCollideToDestinationHelper(entity, collideBounds, location.Position, desiredDestination, null, out EntityCollision collision, entityCollisionList);
            entityCollisionList.Sort();

            if (collision.OtherEntity != null)
            {
                while (entityCollisionList.Count > 0 && entityCollisionList[^1].Time > collision.Time)
                    entityCollisionList.RemoveAt(entityCollisionList.Count - 1);
                velocity *= collision.Time;
            }

            if (!sliding && Vector3.IsNearZero(velocity)) return false;

            collidedDestination = location.Position + velocity;

            if (sliding && collision.OtherEntity != null)
            {
                Vector3 normal2D = Vector3.SafeNormalize2D(collision.Normal, Vector3.Zero);
                Vector3 slidingVelocity = desiredDestination - collidedDestination;
                Vector3 slidingVelocity2D = new (slidingVelocity.X, slidingVelocity.Y, 0.0f);

                float dot = Vector3.Dot(slidingVelocity2D, normal2D);
                if (dot < 0.0f)
                {
                    slidingVelocity2D -= normal2D * dot;

                    var locomotor = entity.Locomotor;
                    if (locomotor == null) return false;

                    Vector3 newDesiredDestination = new();
                    locomotor.SweepFromTo(collidedDestination, collidedDestination + slidingVelocity2D, ref newDesiredDestination);

                    Vector3 newVelocity = newDesiredDestination - collidedDestination;
                    if (Vector3.IsNearZero(newVelocity) == false)
                    {
                        SweepEntityCollideToDestinationHelper(entity, collideBounds, collidedDestination, newDesiredDestination, collision.OtherEntity, out EntityCollision newCollision, entityCollisionList);
                        collidedDestination += newVelocity * newCollision.Time;
                    }
                }
                return !Vector3.IsNearZero(collidedDestination - location.Position);
            }
            else
                return true;
        }

        private void SweepEntityCollideToDestinationHelper(WorldEntity entity, Aabb volume, Vector3 position, Vector3 destination, WorldEntity blockedEntity, out EntityCollision outCollision, List<EntityCollision> entityCollisionList)
        {
            Bounds bounds = entity.EntityCollideBounds;
            RegionLocation location = entity.RegionLocation;
            Vector3 velocity = destination - position;
            Vector3 velocity2D = new (velocity.X, velocity.Y, 0.0f);
            outCollision = new();
            var context = entity.GetEntityRegionSPContext();
            foreach (var otherEntity in entity.Region.IterateEntitiesInVolume(volume, context))
                if (entity != otherEntity && blockedEntity != otherEntity)
                {
                    if (entity.CanCollideWith(otherEntity) || otherEntity.CanCollideWith(entity))
                    {
                        Bounds otherBounds = otherEntity.EntityCollideBounds;

                        float time = 1.0f;
                        Vector3 normal = Vector3.ZAxis;
                        if (bounds.Sweep(otherBounds, Vector3.Zero, velocity, ref time, ref normal) == false) continue;

                        velocity *= time;
                        EntityCollision entityCollision = new ()
                        {
                            OtherEntity = otherEntity,
                            Time = time,
                            Position = location.Position + velocity,
                            Normal = normal
                        };
                        entityCollisionList.Add(entityCollision);

                        if (entity.CanBeBlockedBy(otherEntity))
                        {
                            float dot = Vector3.Dot(velocity2D, normal);
                            if (dot < 0.0f && (outCollision.OtherEntity == null || time < outCollision.Time))
                                outCollision = entityCollision;
                        }
                    }
                }
           
        }


        private bool GetDesiredDestination(WorldEntity entity, Vector3 vector, bool allowSweep, ref Vector3 resultPosition, out bool clipped)
        {
            RegionLocation location = entity.RegionLocation;
            Vector3 destination = location.Position + vector;
            clipped = false;
            Locomotor locomotor = entity.Locomotor;
            if (locomotor == null)
            {
                resultPosition = location.Position;
                return true;
            }

            Vector3 resultNormal = Vector3.ZAxis;
            SweepResult sweepResult = locomotor.SweepTo(destination, ref resultPosition, ref resultNormal);
            if (sweepResult == SweepResult.Failed) return false;
            clipped = (sweepResult != SweepResult.Success);

            Vector3 resultNormal2D = Vector3.SafeNormalize2D(resultNormal, Vector3.Zero);

            if (locomotor.IsMissile)
                resultPosition.Z = destination.Z;

            if (clipped && Vector3.IsNearZero(location.Position - resultPosition))
                resultPosition += resultNormal2D * 0.1f;

            Region region = entity.Region;
            if (region != null)
                resultPosition.Z = Math.Clamp(resultPosition.Z, region.Bound.Min.Z, region.Bound.Max.Z);

            if (clipped && allowSweep)
            {
                Vector3 velocity = destination - resultPosition;
                Vector3 velocity2D = new (velocity.X, velocity.Y, 0.0f);

                float dot = Vector3.Dot(velocity2D, resultNormal2D);
                if (dot < 0.0f)
                {
                    velocity2D += resultNormal2D * (-dot);

                    Vector3 fromPosition = new(resultPosition);
                    destination = resultPosition + velocity2D;

                    sweepResult = locomotor.SweepFromTo(fromPosition, destination, ref resultPosition);
                    if (sweepResult == SweepResult.Failed) return false;
                }
            }

            if (Vector3.IsNearZero(resultPosition - location.Position)) return false;

            return true;
        }


        private void NotifyEntityCollision(WorldEntity who, WorldEntity other, Vector3 collidedDestination)
        {
            throw new NotImplementedException();
        }

        private void HandleEntityCollisions(List<EntityCollision> entityCollisionList, WorldEntity entity, bool applyRepulsionForces)
        {
            throw new NotImplementedException();
        }

        private void CheckForExistingCollisions(WorldEntity entity, bool applyRepulsionForces)
        {
            if (entity == null) return;
            Region region = entity.Region;
            if (region == null) return;

            Aabb bound = entity.EntityCollideBounds.ToAabb();            
            Vector3 position = entity.RegionLocation.Position;

            List<WorldEntity> collisions = new();
            var context = entity.GetEntityRegionSPContext();
            foreach (var otherEntity in region.IterateEntitiesInVolume(bound, context))
                if (entity != otherEntity)
                    collisions.Add(otherEntity);

            foreach (var otherEntity in collisions)
            {
                EntityCollision entityCollision = new ()
                {
                    OtherEntity = otherEntity,
                    Time = 0.0f,
                    Position = position,
                    Normal = Vector3.ZAxis
                };
                HandlePossibleEntityCollision(entity, entityCollision, applyRepulsionForces, true);
            }
        }

        private void HandlePossibleEntityCollision(WorldEntity entity, EntityCollision entityCollision, bool applyRepulsionForces, bool boundsCheck)
        {
            if (entity == null || entityCollision.OtherEntity == null) return;

            WorldEntity otherEntity = entityCollision.OtherEntity;

            if (CacheCollisionPair(entity, otherEntity) == false) return;

            EntityPhysics entityPhysics = entity.Physics;
            EntityPhysics otherPhysics = otherEntity.Physics;

            if (entity.CanBeBlockedBy(otherEntity))
            {
                if (boundsCheck)
                {
                    Bounds bounds = entity.EntityCollideBounds;
                    Bounds otherBounds = otherEntity.EntityCollideBounds;
                    if (bounds.Intersects(otherBounds) == false) return;
                }

                if (applyRepulsionForces)
                    ApplyRepulsionForces(entity, otherEntity);

                NotifyEntityCollision(entity, otherEntity, entityCollision.Position);

                if (otherEntity.CanCollideWith(entity))
                    NotifyEntityCollision(otherEntity, entity, otherEntity.RegionLocation.Position);
            }
            else if (entity.CanCollideWith(otherEntity) || otherEntity.CanCollideWith(entity))
            {
                if (boundsCheck)
                {
                    Bounds bounds = entity.EntityCollideBounds;
                    Bounds otherBounds = otherEntity.EntityCollideBounds;
                    if (bounds.Intersects(otherBounds) == false) return;
                }

                if (entityPhysics.IsTrackingOverlap() || otherPhysics.IsTrackingOverlap())
                {
                    UpdateOverlapEntryHelper(entityPhysics, otherEntity);
                    UpdateOverlapEntryHelper(otherPhysics, entity);

                    Vector3 entityPosition = entityCollision.Position;
                    Vector3 otherEntityPosition = otherEntity.RegionLocation.Position;
                    ResolveOverlapEvent(OverlapEventType.Update, entity, otherEntity, entityPosition, otherEntityPosition);
                    ResolveOverlapEvent(OverlapEventType.Update, otherEntity, entity, otherEntityPosition, entityPosition);
                }
            }
        }

        private void ResolveOverlapEvent(OverlapEventType type, WorldEntity who, WorldEntity whom, Vector3 whoPos, Vector3 whomPos)
        {
            if (who == null || whom == null) return;
            if (who.IsInWorld == false || whom.IsInWorld == false) return;

            if (type == OverlapEventType.Update)
            {
                if (who.Physics.OverlappedEntities.TryGetValue(whom.Id, out var overlappedEntity))
                {
                    bool overlapped = who.CanCollideWith(whom);
                    if (overlappedEntity.Overlapped != overlapped)
                    {
                        overlappedEntity.Overlapped = overlapped;
                        if (overlapped)
                            NotifyEntityOverlapBegin(who, whom, whoPos, whomPos);
                        else
                            NotifyEntityOverlapEnd(who, whom);
                    }
                }
            }
            else if (type == OverlapEventType.Remove)
            {
                if (who.Physics.OverlappedEntities.TryGetValue(whom.Id, out var overlappedEntity))
                {
                    bool overlapped = overlappedEntity.Overlapped;
                    who.Physics.OverlappedEntities.Remove(whom.Id);
                    if (overlapped)
                        NotifyEntityOverlapEnd(who, whom);
                }
            }
        }

        private void NotifyEntityOverlapEnd(WorldEntity who, WorldEntity whom)
        {
            throw new NotImplementedException();
        }

        private void NotifyEntityOverlapBegin(WorldEntity who, WorldEntity whom, Vector3 whoPos, Vector3 whomPos)
        {
            throw new NotImplementedException();
        }

        private void UpdateOverlapEntryHelper(EntityPhysics entityPhysics, WorldEntity otherEntity)
        {
            throw new NotImplementedException();
        }

        private void ApplyRepulsionForces(WorldEntity entity, WorldEntity otherEntity)
        {
            throw new NotImplementedException();
        }

        private bool CacheCollisionPair(WorldEntity entity, WorldEntity otherEntity)
        {
            int collisionId = entity.Physics.CollisionId;
            int otherCollisionId = otherEntity.Physics.CollisionId;

            if (collisionId == -1 || otherCollisionId == -1) return false;

            Region region = entity.Region;
            if (region == null) return false;

            if (entity.Id < otherEntity.Id)
                return region.CollideEntities(collisionId, otherCollisionId); 
            else
                return region.CollideEntities(otherCollisionId, collisionId);
        }

        private void SwapCurrentForceReadWriteIndices()
        {
            _currentForceReadWriteState = !_currentForceReadWriteState;
        }

        public void RegisterEntityForPendingPhysicsResolve(WorldEntity worldEntity)
        {
            if (worldEntity == null) return;
            var entityPhysics = worldEntity.Physics;
            if (entityPhysics.RegisteredPhysicsFrameId != _physicsFrames)
            {
                entityPhysics.RegisteredPhysicsFrameId = _physicsFrames;
                _entitiesPendingResolve.Add(worldEntity.Id);
            }
        }

    }

    [Flags]
    public enum MoveEntityFlags
    {
        SendToOwner = 1 << 0,
        SweepCollide = 1 << 2,
        Sliding = 1 << 3,
        SendToClients = 1 << 4,
    }

    public class PhysicsContext
    {
        public List<WorldEntity> AttachedEntities;
    }

    public enum OverlapEventType
    {
        Update,
        Remove
    }

    public class OverlapEvent
    {
        public OverlapEventType Type;
        public WorldEntity Who;
        public WorldEntity Whom;
        public Vector3 WhoPos;
        public Vector3 WhomPos;

        public OverlapEvent(OverlapEventType type, WorldEntity who, WorldEntity whom)
        {
            Type = type;
            Who = who;
            Whom = whom;
            WhoPos = Vector3.Zero;
            WhomPos = Vector3.Zero;
        }
    }

    public class EntityCollision
    {
        public WorldEntity OtherEntity { get; internal set; }
        public float Time { get; internal set; }
        public Vector3 Position { get; internal set; }
        public Vector3 Normal { get; internal set; }

        public EntityCollision()
        {
            OtherEntity = null;
            Time = 1.0f;
        }

        public EntityCollision(WorldEntity otherEntity, float time, Vector3 position, Vector3 normal)
        {
            OtherEntity = otherEntity;
            Time = time;
            Position = position;
            Normal = normal;
        }

        public int CompareTo(EntityCollision other)
        {
            return Time.CompareTo(other.Time);
        }
    }
}
