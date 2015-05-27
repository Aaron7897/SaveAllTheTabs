﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Newtonsoft.Json;
using SaveAllTheTabs.Polyfills;

namespace SaveAllTheTabs
{
    internal interface IDocumentManager
    {
        event EventHandler GroupsReset;

        ObservableCollection<DocumentGroup> Groups { get; }

        int GroupCount { get; }
        bool HasSlotGroups { get; }

        DocumentGroup GetGroup(int slot);
        DocumentGroup GetGroup(string name);
        DocumentGroup GetSelectedGroup();

        void SaveGroup(string name, int? slot = null);

        void RestoreGroup(int slot, bool reset = false);
        void RestoreGroup(DocumentGroup group, bool reset = false);

        void MoveGroup(DocumentGroup group, int delta);

        void SetGroupSlot(DocumentGroup group, int slot);

        void RemoveGroup(DocumentGroup group);

        void ClearGroups();

        void SaveStashGroup();

        void RestoreStashGroup(bool reset = false);

        bool HasStashGroup { get; }

        int? FindFreeSlot();
    }

    internal class DocumentManager : IDocumentManager
    {
        public event EventHandler GroupsReset;

        public const string StashGroupName = "<stash>";

        private const int SlotMin = 1;
        private const int SlotMax = 9;

        private const string StorageCollectionPath = "SaveAllTheTabs";
        private const string SavedTabsStoragePropertyFormat = "SavedTabs.{0}";
        private const string MigrationStorageCollectionPath = "TabGroups";

        private SaveAllTheTabsPackage Package { get; }
        private IServiceProvider ServiceProvider => Package;
        private IVsUIShellDocumentWindowMgr DocumentWindowMgr { get; }
        private string SolutionName => Package.Environment.Solution?.FullName;

        public ObservableCollection<DocumentGroup> Groups { get; private set; }

        public DocumentManager(SaveAllTheTabsPackage package)
        {
            Package = package;

            package.SolutionChanged += (sender, args) => LoadGroups();
            LoadGroups();

            DocumentWindowMgr = ServiceProvider.GetService(typeof(IVsUIShellDocumentWindowMgr)) as IVsUIShellDocumentWindowMgr;
        }

        private IDisposable _changeSubscription;

        private void LoadGroups()
        {
            _changeSubscription?.Dispose();

            // Load presets for the current solution
            Groups = new TrulyObservableCollection<DocumentGroup>(LoadGroupsForSolution());

            _changeSubscription = new CompositeDisposable
                                  {
                                      Observable.FromEventPattern<NotifyCollectionChangedEventArgs>(Groups, "CollectionChanged")
                                                .Subscribe(re => SaveGroupsForSolution()),

                                      Observable.FromEventPattern<PropertyChangedEventArgs>(Groups, "CollectionItemChanged")
                                                .Where(re => re.EventArgs.PropertyName == "Name" || re.EventArgs.PropertyName == "Slot")
                                                .Throttle(TimeSpan.FromSeconds(1))
                                                .ObserveOnDispatcher()
                                                .Subscribe(re => SaveGroupsForSolution())
                                  };

            GroupsReset?.Invoke(this, EventArgs.Empty);
        }

        public int GroupCount => Groups?.Count ?? 0;
        public bool HasSlotGroups => Groups?.Any(g => g.Slot.HasValue) == true;

        public DocumentGroup GetGroup(int slot) => Groups.FindBySlot(slot);
        public DocumentGroup GetGroup(string name) => Groups.FindByName(name);
        public DocumentGroup GetSelectedGroup() => Groups.SingleOrDefault(g => g.IsSelected);

        public void SaveGroup(string name, int? slot = null)
        {
            if (DocumentWindowMgr == null)
            {
                Debug.Assert(false, "IVsUIShellDocumentWindowMgr", String.Empty, 0);
                return;
            }

            var isStash = name.Equals(StashGroupName, StringComparison.InvariantCultureIgnoreCase);
            if (isStash)
            {
                slot = null;
            }

            var group = Groups.FindByName(name);

            var files = Package.Environment.GetDocumentFiles();//.ToList();
            //var bps = Package.Environment.GetMatchingBreakpoints(new HashSet<string>(files), StringComparer.InvariantCultureIgnoreCase));

            using (var stream = new VsOleStream())
            {
                var hr = DocumentWindowMgr.SaveDocumentWindowPositions(0, stream);
                if (hr != VSConstants.S_OK)
                {
                    Debug.Assert(false, "SaveDocumentWindowPositions", String.Empty, hr);

                    if (group != null)
                    {
                        Groups.Remove(group);
                    }
                    return;
                }
                stream.Seek(0, SeekOrigin.Begin);

                var documents = String.Join(", ", files.Select(Path.GetFileName));

                if (group == null)
                {
                    group = new DocumentGroup
                    {
                        Name = name,
                        Description = documents,
                        Positions = stream.ToArray()
                    };

                    TrySetSlot(group, slot);
                    if (isStash)
                    {
                        Groups.Insert(0, group);
                    }
                    else
                    {
                        Groups.Add(group);
                    }
                }
                else
                {
                    group.Description = documents;
                    group.Positions = stream.ToArray();

                    TrySetSlot(group, slot);
                }
            }
        }

        private void TrySetSlot(DocumentGroup group, int? slot)
        {
            if (!slot.HasValue || !(slot <= SlotMax) || group.Slot == slot)
            {
                return;
            }

            // Find out if there is an existing item in the desired slot
            var resident = Groups.FindBySlot((int)slot);
            if (resident != null)
            {
                resident.Slot = null;
            }

            group.Slot = slot;
        }

        public void RestoreGroup(int slot, bool reset = false)
        {
            RestoreGroup(Groups.FindBySlot(slot), reset);
        }

        public void RestoreGroup(DocumentGroup group, bool reset = false)
        {
            if (group == null)
            {
                return;
            }

            if (reset)
            {
                Package.Environment.GetDocumentWindows().CloseAll();
            }

            using (var stream = new VsOleStream())
            {
                stream.Write(group.Positions, 0, group.Positions.Length);
                stream.Seek(0, SeekOrigin.Begin);

                var hr = DocumentWindowMgr.ReopenDocumentWindows(stream);
                if (hr != VSConstants.S_OK)
                {
                    Debug.Assert(false, "ReopenDocumentWindows", String.Empty, hr);
                }
            }
        }

        public void MoveGroup(DocumentGroup group, int delta)
        {
            if (group == null)
            {
                return;
            }

            var index = Groups.IndexOf(group);
            var newIndex = index + delta;
            if (newIndex == 0 || newIndex >= Groups.Count)
            {
                return;
            }

            Groups.Move(index, newIndex);
        }

        public void SetGroupSlot(DocumentGroup group, int slot)
        {
            if (group == null || group.Slot == slot)
            {
                return;
            }

            var resident = Groups.FindBySlot(slot);

            group.Slot = slot;

            if (resident != null)
            {
                resident.Slot = null;
            }
        }

        public void RemoveGroup(DocumentGroup group)
        {
            if (group == null)
            {
                return;
            }

            Groups.Remove(group);
        }

        public void ClearGroups()
        {
            Groups.Clear();
        }

        public void SaveStashGroup()
        {
            SaveGroup(StashGroupName);
        }

        public void RestoreStashGroup(bool reset = false)
        {
            RestoreGroup(Groups.FindByName(StashGroupName), reset);
        }

        public bool HasStashGroup => Groups.FindByName(StashGroupName) != null;

        public int? FindFreeSlot()
        {
            var slotted = Groups.Where(g => g.Slot.HasValue)
                                .OrderBy(g => g.Slot)
                                .ToList();

            if (!slotted.Any())
            {
                return SlotMin;
            }

            for (var i = SlotMin; i <= SlotMax; i++)
            {
                if (slotted.All(g => g.Slot != i))
                {
                    return i;
                }
            }

            return null;
        }

        private List<DocumentGroup> LoadGroupsForSolution()
        {
            var solution = SolutionName;
            if (!string.IsNullOrWhiteSpace(solution))
            {
                try
                {
                    var settingsMgr = new ShellSettingsManager(ServiceProvider);
                    var store = settingsMgr.GetReadOnlySettingsStore(SettingsScope.UserSettings);

                    var propertyName = String.Format(SavedTabsStoragePropertyFormat, solution);
                    if (store.PropertyExists(StorageCollectionPath, propertyName))
                    {
                        var tabs = store.GetString(StorageCollectionPath, propertyName);
                        return JsonConvert.DeserializeObject<List<DocumentGroup>>(tabs);
                    }

                    // Migrate old settings
                    if (store.CollectionExists(MigrationStorageCollectionPath) &&
                        store.PropertyExists(MigrationStorageCollectionPath, solution))
                    {
                        var tabs = store.GetString(MigrationStorageCollectionPath, solution);
                        var groups = JsonConvert.DeserializeObject<List<DocumentGroup>>(tabs);

                        SaveGroupsForSolution(groups);

                        return groups;
                    }
                }
                catch (Exception) { }
            }
            return new List<DocumentGroup>();
        }

        private void SaveGroupsForSolution(IList<DocumentGroup> groups = null)
        {
            var solution = SolutionName;
            if (string.IsNullOrWhiteSpace(solution))
            {
                return;
            }

            if (groups == null)
            {
                groups = Groups;
            }

            var settingsMgr = new ShellSettingsManager(ServiceProvider);
            var store = settingsMgr.GetWritableSettingsStore(SettingsScope.UserSettings);

            if (!store.CollectionExists(StorageCollectionPath))
            {
                store.CreateCollection(StorageCollectionPath);
            }

            var propertyName = String.Format(SavedTabsStoragePropertyFormat, solution);
            if (!groups.Any())
            {
                store.DeleteProperty(StorageCollectionPath, propertyName);
                return;
            }

            var tabs = JsonConvert.SerializeObject(groups);
            store.SetString(StorageCollectionPath, propertyName, tabs);
        }
    }
}
