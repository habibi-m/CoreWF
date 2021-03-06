// This file is part of Core WF which is licensed under the MIT license.
// See LICENSE file in the project root for full license information.

namespace System.Activities.Runtime
{
    using System;
    using System.Runtime.Serialization;
    using System.Collections.Generic;
    using System.Activities.Internals;

#if NET45
    using System.Activities.DynamicUpdate;

#endif
    [DataContract]
    internal sealed class LocationEnvironment
#if NET45
                : ActivityInstanceMap.IActivityReferenceWithEnvironment
#else
                : ActivityInstanceMap.IActivityReference
#endif
    {
        private static readonly DummyLocation dummyLocation = new DummyLocation();
        private bool isDisposed;
        private bool hasHandles;
        private ActivityExecutor executor;

        // These two fields should be null unless we're in between calls to Update() and OnDeserialized().
        // Therefore they should never need to serialize.
        private IList<Location> locationsToUnregister;
        private IList<LocationReference> locationsToRegister;
        private Location[] locations;
        private bool hasMappableLocations;
        private LocationEnvironment parent;
        private Location singleLocation;

        // This list keeps track of handles that are created and initialized.
        private List<Handle> handles;

        // We store refCount - 1 because it is more likely to
        // be zero and skipped by serialization
        private int referenceCountMinusOne;
        private bool hasOwnerCompleted;

        internal LocationEnvironment() { }

        // this ctor overload is to be exclusively used by DU
        // for creating a LocationEnvironment for "noSymbols" ActivityInstance
        internal LocationEnvironment(LocationEnvironment parent, int capacity) 
            : this(null, null, parent, capacity)
        {
        }
       
        internal LocationEnvironment(ActivityExecutor executor, Activity definition)
        {
            this.executor = executor;
            this.Definition = definition;
        }

        internal LocationEnvironment(ActivityExecutor executor, Activity definition, LocationEnvironment parent, int capacity)
            : this(executor, definition)
        {
            this.parent = parent;

            Fx.Assert(capacity > 0, "must have a positive capacity if using this overload");
            if (capacity > 1)
            {
                this.locations = new Location[capacity];
            }
        }

        [DataMember(EmitDefaultValue = false, Name = "locations")]
        internal Location[] SerializedLocations
        {
            get { return this.locations; }
            set { this.locations = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "hasMappableLocations")]
        internal bool SerializedHasMappableLocations
        {
            get { return this.hasMappableLocations; }
            set { this.hasMappableLocations = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "parent")]
        internal LocationEnvironment SerializedParent
        {
            get { return this.parent; }
            set { this.parent = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "singleLocation")]
        internal Location SerializedSingleLocation
        {
            get { return this.singleLocation; }
            set { this.singleLocation = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "handles")]
        internal List<Handle> SerializedHandles
        {
            get { return this.handles; }
            set { this.handles = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "referenceCountMinusOne")]
        internal int SerializedReferenceCountMinusOne
        {
            get { return this.referenceCountMinusOne; }
            set { this.referenceCountMinusOne = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "hasOwnerCompleted")]
        internal bool SerializedHasOwnerCompleted
        {
            get { return this.hasOwnerCompleted; }
            set { this.hasOwnerCompleted = value; }
        }

        internal Activity Definition
        {
            get;
            private set;
        }

        internal LocationEnvironment Parent
        {
            get
            {
                return this.parent;
            }
            set
            {
                this.parent = value;
            }
        }

        internal bool HasHandles
        {
            get
            {
                return this.hasHandles;
            }
        }

        private MappableObjectManager MappableObjectManager
        {
            get
            {
                return this.executor.MappableObjectManager;
            }
        }

        internal bool ShouldDispose
        {
            get
            {
                return this.referenceCountMinusOne == -1;
            }
        }

        internal bool HasOwnerCompleted
        {
            get
            {
                return this.hasOwnerCompleted;
            }
        }

        Activity ActivityInstanceMap.IActivityReference.Activity
        {
            get
            {
                return this.Definition;
            }
        }

        internal List<Handle> Handles
        {
            get { return this.handles; }
        }

        void ActivityInstanceMap.IActivityReference.Load(Activity activity, ActivityInstanceMap instanceMap)
        {
            this.Definition = activity;
        }

#if NET45
        void ActivityInstanceMap.IActivityReferenceWithEnvironment.UpdateEnvironment(EnvironmentUpdateMap map, Activity activity)
        {
            // LocationEnvironment.Update() is invoked through this path when this is a seondary root's environment(and in its parent chain) whose owner has already completed.
            this.Update(map, activity);
        }    
#endif

        // Note that the owner should never call this as the first
        // AddReference is assumed
        internal void AddReference()
        {
            this.referenceCountMinusOne++;
        }

        internal void RemoveReference(bool isOwner)
        {
            if (isOwner)
            {
                this.hasOwnerCompleted = true;
            }

            Fx.Assert(this.referenceCountMinusOne >= 0, "We must at least have 1 reference (0 for refCountMinusOne)");
            this.referenceCountMinusOne--;
        }

        internal void OnDeserialized(ActivityExecutor executor, ActivityInstance handleScope)
        {
            this.executor = executor;

            // The instance map Load might have already set the definition to the correct one.
            // If not then we assume the definition is the same as the handle scope.
            if (this.Definition == null)
            {
                this.Definition = handleScope.Activity;
            }

            ReinitializeHandles(handleScope);
            RegisterUpdatedLocations(handleScope);
        }

        internal void ReinitializeHandles(ActivityInstance handleScope)
        {
            // Need to reinitialize the handles in the list.
            if (this.handles != null)
            {
                int count = this.handles.Count;
                for (int i = 0; i < count; i++)
                {
                    this.handles[i].Reinitialize(handleScope);
                    this.hasHandles = true;
                }
            }
        }

        internal void Dispose()
        {
            Fx.Assert(this.ShouldDispose, "We shouldn't be calling Dispose when we have existing references.");
            Fx.Assert(!this.hasHandles, "We should have already uninitialized the handles and set our hasHandles variable to false.");
            Fx.Assert(!this.isDisposed, "We should not already be disposed.");

            this.isDisposed = true;

            CleanupMappedLocations();
        }

        internal void AddHandle(Handle handleToAdd)
        {
            if (this.handles == null)
            {
                this.handles = new List<Handle>();
            }
            this.handles.Add(handleToAdd);
            this.hasHandles = true;
        }

        private void CleanupMappedLocations()
        {
            if (this.hasMappableLocations)
            {
                if (this.singleLocation != null)
                {
                    Fx.Assert(this.singleLocation.CanBeMapped, "Can only have mappable locations for a singleton if its mappable.");
                    UnregisterLocation(this.singleLocation);
                }
                else if (this.locations != null)
                {
                    for (int i = 0; i < this.locations.Length; i++)
                    {
                        Location location = this.locations[i];

                        if (location.CanBeMapped)
                        {
                            UnregisterLocation(location);
                        }
                    }
                }
            }
        }

        internal void UninitializeHandles(ActivityInstance scope)
        {
            if (this.hasHandles)
            {
                HandleInitializationContext context = null;

                try
                {
                    UninitializeHandles(scope, this.Definition.RuntimeVariables, ref context);
                    UninitializeHandles(scope, this.Definition.ImplementationVariables, ref context);

                    this.hasHandles = false;
                }
                finally
                {
                    if (context != null)
                    {
                        context.Dispose();
                    }
                }
            }
        }

        private void UninitializeHandles(ActivityInstance scope, IList<Variable> variables, ref HandleInitializationContext context)
        {
            for (int i = 0; i < variables.Count; i++)
            {
                Variable variable = variables[i];
                Fx.Assert(variable.Owner == this.Definition, "We should only be targeting the vairables at this scope.");

                if (variable.IsHandle)
                {
                    Location location = GetSpecificLocation(variable.Id);

                    if (location != null)
                    {
                        Handle handle = (Handle)location.Value;

                        if (handle != null)
                        {
                            if (context == null)
                            {
                                context = new HandleInitializationContext(this.executor, scope);
                            }

                            handle.Uninitialize(context);
                        }

                        location.Value = null;
                    }
                }
            }
        }

        internal void DeclareHandle(LocationReference locationReference, Location location, ActivityInstance activityInstance)
        {
            this.hasHandles = true;

            Declare(locationReference, location, activityInstance);
        }

        internal void DeclareTemporaryLocation<T>(LocationReference locationReference, ActivityInstance activityInstance, bool bufferGetsOnCollapse)
            where T : Location
        {
            Location locationToDeclare = new Location<T>();
            locationToDeclare.SetTemporaryResolutionData(this, bufferGetsOnCollapse);

            this.Declare(locationReference, locationToDeclare, activityInstance);
        }

        internal void Declare(LocationReference locationReference, Location location, ActivityInstance activityInstance)
        {
            Fx.Assert((locationReference.Id == 0 && this.locations == null) || (locationReference.Id >= 0 && this.locations != null && locationReference.Id < this.locations.Length), "The environment should have been created with the appropriate capacity.");
            Fx.Assert(location != null, "");

            RegisterLocation(location, locationReference, activityInstance);

            if (this.locations == null)
            {
                Fx.Assert(this.singleLocation == null, "We should not have had a single location if we are trying to declare one.");
                Fx.Assert(locationReference.Id == 0, "We should think the id is zero if we are setting the single location.");

                this.singleLocation = location;
            }
            else
            {
                Fx.Assert(this.locations[locationReference.Id] == null || this.locations[locationReference.Id] is DummyLocation, "We should not have had a location at the spot we are replacing.");

                this.locations[locationReference.Id] = location;
            }
        }

        internal Location<T> GetSpecificLocation<T>(int id)
        {
            return GetSpecificLocation(id) as Location<T>;
        }

        internal Location GetSpecificLocation(int id)
        {
            Fx.Assert(id >= 0 && ((this.locations == null && id == 0) || (this.locations != null && id < this.locations.Length)), "Id needs to be within bounds.");

            if (this.locations == null)
            {
                return this.singleLocation;
            }
            else
            {
                return this.locations[id];
            }
        }

        // called for asynchronous argument resolution to collapse Location<Location<T>> to Location<T> in the environment
        internal void CollapseTemporaryResolutionLocations()
        {
            if (this.locations == null)
            {
                if (this.singleLocation != null &&
                    object.ReferenceEquals(this.singleLocation.TemporaryResolutionEnvironment, this))
                {
                    CollapseTemporaryResolutionLocation(ref this.singleLocation);
                }
            }
            else
            {
                for (int i = 0; i < this.locations.Length; i++)
                {
                    Location referenceLocation = this.locations[i];

                    if (referenceLocation != null &&
                        object.ReferenceEquals(referenceLocation.TemporaryResolutionEnvironment, this))
                    {
                        CollapseTemporaryResolutionLocation(ref this.locations[i]);
                    }
                }
            }
        }

        // Called after an argument is added in Dynamic Update, when we need to collapse
        // just one location rather than the whole environment
        internal void CollapseTemporaryResolutionLocation(Location location)
        {
            // This assert doesn't necessarily imply that the location is still part of this environment;
            // it might have been removed in a subsequent update. If so, this method is a no-op.
            Fx.Assert(location.TemporaryResolutionEnvironment == this, "Trying to collapse from the wrong environment");

            if (this.singleLocation == location)
            {
                CollapseTemporaryResolutionLocation(ref this.singleLocation);
            }
            else if (this.locations != null)
            {
                for (int i = 0; i < this.locations.Length; i++)
                {
                    if (this.locations[i] == location)
                    {
                        CollapseTemporaryResolutionLocation(ref this.locations[i]);
                    }
                }
            }
        }

        private void CollapseTemporaryResolutionLocation(ref Location location)
        {
            if (location.Value == null)
            {
                location = (Location)location.CreateDefaultValue();
            }
            else
            {
                location = ((Location)location.Value).CreateReference(location.BufferGetsOnCollapse);
            }
        }

        private void RegisterUpdatedLocations(ActivityInstance activityInstance)
        {
            if (this.locationsToRegister != null)
            {
                foreach (LocationReference locationReference in this.locationsToRegister)
                {
                    RegisterLocation(GetSpecificLocation(locationReference.Id), locationReference, activityInstance);
                }
                this.locationsToRegister = null;
            }

            if (this.locationsToUnregister != null)
            {
                foreach (Location location in this.locationsToUnregister)
                {
                    UnregisterLocation(location);
                }
                this.locationsToUnregister = null;
            }
        }

        // Gets the location at this scope.  The caller verifies that ref.owner == this.definition.
        internal bool TryGetLocation(int id, out Location value)
        {
            ThrowIfDisposed();

            value = null;

            if (this.locations == null)
            {
                if (id == 0)
                {
                    value = this.singleLocation;
                }
            }
            else
            {
                if (this.locations.Length > id)
                {
                    value = this.locations[id];
                }
            }

            return value != null;
        }

        internal bool TryGetLocation(int id, Activity environmentOwner, out Location value)
        {
            ThrowIfDisposed();

            LocationEnvironment targetEnvironment = this;

            while (targetEnvironment != null && targetEnvironment.Definition != environmentOwner)
            {
                targetEnvironment = targetEnvironment.Parent;
            }

            if (targetEnvironment == null)
            {
                value = null;
                return false;
            }

            value = null;

            if (id == 0 && targetEnvironment.locations == null)
            {
                value = targetEnvironment.singleLocation;
            }
            else if (targetEnvironment.locations != null && targetEnvironment.locations.Length > id)
            {
                value = targetEnvironment.locations[id];
            }

            return value != null;
        }

        private void RegisterLocation(Location location, LocationReference locationReference, ActivityInstance activityInstance)
        {
            if (location.CanBeMapped)
            {
                this.hasMappableLocations = true;
                this.MappableObjectManager.Register(location, this.Definition, locationReference, activityInstance);
            }
        }

        private void UnregisterLocation(Location location)
        {
            this.MappableObjectManager.Unregister(location);
        }

        private void ThrowIfDisposed()
        {
            if (isDisposed)
            {
                throw FxTrace.Exception.AsError(
                    new ObjectDisposedException(this.GetType().FullName, SR.EnvironmentDisposed));
            }
        }

#if NET45
        internal void Update(EnvironmentUpdateMap map, Activity activity)
        {
            //                    arguments     public variables      private variables    RuntimeDelegateArguments
            //  Locations array:  AAAAAAAAAA   VVVVVVVVVVVVVVVVVVVVVV PPPPPPPPPPPPPPPPPPP  DDDDDDDDDDDDDDDDDDDDDDDDDDDDDD

            int actualRuntimeDelegateArgumentCount = activity.HandlerOf == null ? 0 : activity.HandlerOf.RuntimeDelegateArguments.Count;

            if (map.NewArgumentCount != activity.RuntimeArguments.Count ||
                map.NewVariableCount != activity.RuntimeVariables.Count ||
                map.NewPrivateVariableCount != activity.ImplementationVariables.Count ||
                map.RuntimeDelegateArgumentCount != actualRuntimeDelegateArgumentCount)
            {
                throw FxTrace.Exception.AsError(new InstanceUpdateException(SR.InvalidUpdateMap(
                    SR.WrongEnvironmentCount(activity, map.NewArgumentCount, map.NewVariableCount, map.NewPrivateVariableCount, map.RuntimeDelegateArgumentCount,
                        activity.RuntimeArguments.Count, activity.RuntimeVariables.Count, activity.ImplementationVariables.Count, actualRuntimeDelegateArgumentCount))));
            }

            int expectedLocationCount = map.OldArgumentCount + map.OldVariableCount + map.OldPrivateVariableCount + map.RuntimeDelegateArgumentCount;

            int actualLocationCount;
            if (this.locations == null)
            {
                if (this.singleLocation == null)
                {
                    // we can hit this condition when the root activity instance has zero symbol.
                    actualLocationCount = 0;
                }
                else
                {
                    actualLocationCount = 1;

                    // temporarily normalize to locations array for the sake of environment update processing
                    this.locations = new Location[] { this.singleLocation };
                    this.singleLocation = null;
                }
            }
            else
            {
                Fx.Assert(this.singleLocation == null, "locations and singleLocations cannot be non-null at the same time.");
                actualLocationCount = this.locations.Length;
            }

            if (expectedLocationCount != actualLocationCount)
            {
                throw FxTrace.Exception.AsError(new InstanceUpdateException(SR.InvalidUpdateMap(
                    SR.WrongOriginalEnvironmentCount(activity, map.OldArgumentCount, map.OldVariableCount, map.OldPrivateVariableCount, map.RuntimeDelegateArgumentCount,
                        expectedLocationCount, actualLocationCount))));
            }

            Location[] newLocations = null;

            // If newTotalLocations == 0, update will leave us with an empty LocationEnvironment,
            // which is something the runtime would normally never create. This is harmless, but it
            // is a loosening of normal invariants.
            int newTotalLocations = map.NewArgumentCount + map.NewVariableCount + map.NewPrivateVariableCount + map.RuntimeDelegateArgumentCount;
            if (newTotalLocations > 0)
            {
                newLocations = new Location[newTotalLocations];
            }

            UpdateArguments(map, newLocations);
            UnregisterRemovedVariables(map);
            UpdatePublicVariables(map, newLocations, activity);
            UpdatePrivateVariables(map, newLocations, activity);
            CopyRuntimeDelegateArguments(map, newLocations);

            Location newSingleLocation = null;
            if (newTotalLocations == 1)
            {
                newSingleLocation = newLocations[0];
                newLocations = null;
            }

            this.singleLocation = newSingleLocation;
            this.locations = newLocations;
        }

        void UpdateArguments(EnvironmentUpdateMap map, Location[] newLocations)
        {
            if (map.HasArgumentEntries)
            {
                for (int i = 0; i < map.ArgumentEntries.Count; i++)
                {
                    EnvironmentUpdateMapEntry entry = map.ArgumentEntries[i];

                    Fx.Assert(entry.NewOffset >= 0 && entry.NewOffset < map.NewArgumentCount, "Argument offset is out of range");

                    if (entry.IsAddition)
                    {
                        // Location allocation will be performed later during ResolveDynamicallyAddedArguments().
                        // for now, simply assign a dummy location so we know not to copy over the old value.
                        newLocations[entry.NewOffset] = dummyLocation;
                    }
                    else
                    {
                        Fx.Assert(this.locations != null && this.singleLocation == null, "Caller should have copied singleLocation into locations array");

                        // rearrangement of existing arguments
                        // this entry here doesn't describe argument removal
                        newLocations[entry.NewOffset] = this.locations[entry.OldOffset];
                    }
                }
            }

            // copy over unchanged Locations, and null out DummyLocations
            for (int i = 0; i < map.NewArgumentCount; i++)
            {
                if (newLocations[i] == null)
                {
                    Fx.Assert(this.locations != null && this.locations.Length > i, "locations must be non-null and index i must be within the range of locations.");
                    newLocations[i] = this.locations[i];
                }
                else if (newLocations[i] == dummyLocation)
                {
                    newLocations[i] = null;
                }
            }
        }

        void UpdatePublicVariables(EnvironmentUpdateMap map, Location[] newLocations, Activity activity)
        {
            UpdateVariables(
                map.NewArgumentCount,
                map.OldArgumentCount,
                map.NewVariableCount,
                map.OldVariableCount,
                map.VariableEntries,
                activity.RuntimeVariables,
                newLocations);
        }

        void UpdatePrivateVariables(EnvironmentUpdateMap map, Location[] newLocations, Activity activity)
        {
            UpdateVariables(
                map.NewArgumentCount + map.NewVariableCount,
                map.OldArgumentCount + map.OldVariableCount,
                map.NewPrivateVariableCount,
                map.OldPrivateVariableCount,
                map.PrivateVariableEntries,
                activity.ImplementationVariables,
                newLocations);
        }

        void UpdateVariables(int newVariablesOffset, int oldVariablesOffset, int newVariableCount, int oldVariableCount, IList<EnvironmentUpdateMapEntry> variableEntries, IList<Variable> variables, Location[] newLocations)
        {
            if (variableEntries != null)
            {
                for (int i = 0; i < variableEntries.Count; i++)
                {
                    EnvironmentUpdateMapEntry entry = variableEntries[i];

                    Fx.Assert(entry.NewOffset >= 0 && entry.NewOffset < newVariableCount, "Variable offset is out of range");
                    Fx.Assert(!entry.IsNewHandle, "This should have been caught in ActivityInstanceMap.UpdateRawInstance");

                    if (entry.IsAddition)
                    {
                        Variable newVariable = variables[entry.NewOffset];
                        Location location = newVariable.CreateLocation();
                        newLocations[newVariablesOffset + entry.NewOffset] = location;
                        if (location.CanBeMapped)
                        {
                            ActivityUtilities.Add(ref this.locationsToRegister, newVariable);
                        }
                    }
                    else
                    {
                        Fx.Assert(this.locations != null && this.singleLocation == null, "Caller should have copied singleLocation into locations array");

                        // rearrangement of existing variable
                        // this entry here doesn't describe variable removal
                        newLocations[newVariablesOffset + entry.NewOffset] = this.locations[oldVariablesOffset + entry.OldOffset];
                    }
                }
            }

            // copy over unchanged variable Locations
            for (int i = 0; i < newVariableCount; i++)
            {
                if (newLocations[newVariablesOffset + i] == null)
                {
                    Fx.Assert(i < oldVariableCount, "New variable should have a location");
                    Fx.Assert(this.locations != null && this.locations.Length > oldVariablesOffset + i, "locations must be non-null and index i + oldVariableOffset must be within the range of locations.");

                    newLocations[newVariablesOffset + i] = this.locations[oldVariablesOffset + i];
                }
            }
        }

        void CopyRuntimeDelegateArguments(EnvironmentUpdateMap map, Location[] newLocations)
        {
            for (int i = 1; i <= map.RuntimeDelegateArgumentCount; i++)
            {
                newLocations[newLocations.Length - i] = this.locations[this.locations.Length - i];
            }
        }

        void UnregisterRemovedVariables(EnvironmentUpdateMap map)
        {
            bool hasMappableLocationsRemaining = false;
            int offset = map.OldArgumentCount;

            FindVariablesToUnregister(false, map, map.OldVariableCount, offset, ref hasMappableLocationsRemaining);

            offset = map.OldArgumentCount + map.OldVariableCount;

            FindVariablesToUnregister(true, map, map.OldPrivateVariableCount, offset, ref hasMappableLocationsRemaining);

            this.hasMappableLocations = hasMappableLocationsRemaining;
        }

        delegate int? GetNewVariableIndex(int oldIndex);
        private void FindVariablesToUnregister(bool forImplementation, EnvironmentUpdateMap map, int oldVariableCount, int offset, ref bool hasMappableLocationsRemaining)
        {
            for (int i = 0; i < oldVariableCount; i++)
            {
                Location location = this.locations[i + offset];
                if (location.CanBeMapped)
                {
                    if ((forImplementation && map.GetNewPrivateVariableIndex(i).HasValue) || (!forImplementation && map.GetNewVariableIndex(i).HasValue))
                    {
                        hasMappableLocationsRemaining = true;
                    }
                    else
                    {
                        ActivityUtilities.Add(ref this.locationsToUnregister, location);
                    }
                }
            }
        }

#endif
        private class DummyLocation : Location<object>
        {
            // this is a dummy location 
            // temporarary place holder for a dynamically added LocationReference
        }
    }
}
