﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Library
{
    public class ItemController
    {
        #region PreBeginResolvePath Event
        /// <summary>
        /// Fires when a path is about to be resolved, but before child folders and files 
        /// have been collected from the file system.
        /// This gives listeners a chance to cancel the operation and cause the path to be ignored.
        /// </summary>
        public event EventHandler<PreBeginResolveEventArgs> PreBeginResolvePath;
        private bool OnPreBeginResolvePath(Folder parent, string path, WIN32_FIND_DATA fileData)
        {
            PreBeginResolveEventArgs args = new PreBeginResolveEventArgs()
            {
                Parent = parent,
                File = new LazyFileInfo() { Path = path, FileInfo = fileData },
                Cancel = false
            };

            if (PreBeginResolvePath != null)
            {
                PreBeginResolvePath(this, args);
            }

            return !args.Cancel;
        }
        #endregion

        #region BeginResolvePath Event
        /// <summary>
        /// Fires when a path is about to be resolved, but after child folders and files 
        /// have been collected from the file system.
        /// This gives listeners a chance to cancel the operation and cause the path to be ignored.
        /// </summary>
        public event EventHandler<ItemResolveEventArgs> BeginResolvePath;
        private bool OnBeginResolvePath(ItemResolveEventArgs args)
        {
            if (BeginResolvePath != null)
            {
                BeginResolvePath(this, args);
            }

            return !args.Cancel;
        }
        #endregion

        private BaseItem ResolveItem(ItemResolveEventArgs args)
        {
            // Try first priority resolvers
            for (int i = 0; i < Kernel.Instance.EntityResolvers.Length; i++)
            {
                var item = Kernel.Instance.EntityResolvers[i].ResolvePath(args);

                if (item != null)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a path into a BaseItem
        /// </summary>
        public async Task<BaseItem> GetItem(string path, Folder parent = null, WIN32_FIND_DATA? fileInfo = null)
        {
            WIN32_FIND_DATA fileData = fileInfo ?? FileData.GetFileData(path);

            if (!OnPreBeginResolvePath(parent, path, fileData))
            {
                return null;
            }

            LazyFileInfo[] fileSystemChildren;

            // Gather child folder and files
            if (fileData.IsDirectory)
            {
                fileSystemChildren = ConvertFileSystemEntries(Directory.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly));

                bool isVirtualFolder = parent != null && parent.IsRoot;
                fileSystemChildren = FilterChildFileSystemEntries(fileSystemChildren, isVirtualFolder);
            }
            else
            {
                fileSystemChildren = new LazyFileInfo[] { };
            }

            ItemResolveEventArgs args = new ItemResolveEventArgs()
            {
                File = new LazyFileInfo() { Path = path, FileInfo = fileData },
                FileSystemChildren = fileSystemChildren,
                Parent = parent,
                Cancel = false
            };

            // Fire BeginResolvePath to see if anyone wants to cancel this operation
            if (!OnBeginResolvePath(args))
            {
                return null;
            }

            BaseItem item = ResolveItem(args);

            if (item != null)
            {
                await Kernel.Instance.ExecuteMetadataProviders(item, args);

                var folder = item as Folder;

                if (folder != null)
                {
                    // If it's a folder look for child entities
                    await AttachChildren(folder, fileSystemChildren).ConfigureAwait(false);
                }
            }

            return item;
        }

        /// <summary>
        /// Finds child BaseItems for a given Folder
        /// </summary>
        private async Task AttachChildren(Folder folder, LazyFileInfo[] fileSystemChildren)
        {
            int count = fileSystemChildren.Length;

            Task<BaseItem>[] tasks = new Task<BaseItem>[count];

            for (int i = 0; i < count; i++)
            {
                var child = fileSystemChildren[i];

                tasks[i] = GetItem(child.Path, folder, child.FileInfo);
            }

            BaseItem[] baseItemChildren = await Task<BaseItem>.WhenAll(tasks).ConfigureAwait(false);

            // Sort them
            folder.Children = baseItemChildren.Where(i => i != null).OrderBy(f =>
            {
                return string.IsNullOrEmpty(f.SortName) ? f.Name : f.SortName;

            });
        }

        /// <summary>
        /// Transforms shortcuts into their actual paths
        /// </summary>
        private LazyFileInfo[] FilterChildFileSystemEntries(LazyFileInfo[] fileSystemChildren, bool flattenShortcuts)
        {
            LazyFileInfo[] returnArray = new LazyFileInfo[fileSystemChildren.Length];
            List<LazyFileInfo> resolvedShortcuts = new List<LazyFileInfo>();

            for (int i = 0; i < fileSystemChildren.Length; i++)
            {
                LazyFileInfo file = fileSystemChildren[i];

                // If it's a shortcut, resolve it
                if (Shortcut.IsShortcut(file.Path))
                {
                    string newPath = Shortcut.ResolveShortcut(file.Path);
                    WIN32_FIND_DATA newPathData = FileData.GetFileData(newPath);

                    // Find out if the shortcut is pointing to a directory or file
                    if (newPathData.IsDirectory)
                    {
                        // If we're flattening then get the shortcut's children

                        if (flattenShortcuts)
                        {
                            returnArray[i] = file;
                            LazyFileInfo[] newChildren = ConvertFileSystemEntries(Directory.GetFileSystemEntries(newPath, "*", SearchOption.TopDirectoryOnly));

                            resolvedShortcuts.AddRange(FilterChildFileSystemEntries(newChildren, false));
                        }
                        else
                        {
                            returnArray[i] = new LazyFileInfo() { Path = newPath, FileInfo = newPathData };
                        }
                    }
                    else
                    {
                        returnArray[i] = new LazyFileInfo() { Path = newPath, FileInfo = newPathData };
                    }
                }
                else
                {
                    returnArray[i] = file;
                }
            }

            if (resolvedShortcuts.Count > 0)
            {
                resolvedShortcuts.InsertRange(0, returnArray);
                return resolvedShortcuts.ToArray();
            }
            else
            {
                return returnArray;
            }
        }

        /// <summary>
        /// Gets a Person
        /// </summary>
        public async Task<Person> GetPerson(string name)
        {
            string path = Path.Combine(Kernel.Instance.ApplicationPaths.PeoplePath, name);

            return await GetImagesByNameItem<Person>(path, name).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a Studio
        /// </summary>
        public async Task<Studio> GetStudio(string name)
        {
            string path = Path.Combine(Kernel.Instance.ApplicationPaths.StudioPath, name);

            return await GetImagesByNameItem<Studio>(path, name).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a Genre
        /// </summary>
        public async Task<Genre> GetGenre(string name)
        {
            string path = Path.Combine(Kernel.Instance.ApplicationPaths.GenrePath, name);

            return await GetImagesByNameItem<Genre>(path, name).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a Year
        /// </summary>
        public async Task<Year> GetYear(int value)
        {
            string path = Path.Combine(Kernel.Instance.ApplicationPaths.YearPath, value.ToString());

            return await GetImagesByNameItem<Year>(path, value.ToString()).ConfigureAwait(false);
        }

        private ConcurrentDictionary<string, object> ImagesByNameItemCache = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// Generically retrieves an IBN item
        /// </summary>
        private async Task<T> GetImagesByNameItem<T>(string path, string name)
            where T : BaseEntity, new()
        {
            string key = path.ToLower();

            // Look for it in the cache, if it's not there, create it
            if (!ImagesByNameItemCache.ContainsKey(key))
            {
                T obj = await CreateImagesByNameItem<T>(path, name).ConfigureAwait(false);
                ImagesByNameItemCache[key] = obj;
                return obj;
            }

            return ImagesByNameItemCache[key] as T;
        }

        /// <summary>
        /// Creates an IBN item based on a given path
        /// </summary>
        private async Task<T> CreateImagesByNameItem<T>(string path, string name)
            where T : BaseEntity, new()
        {
            T item = new T();

            item.Name = name;
            item.Id = Kernel.GetMD5(path);

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            item.DateCreated = Directory.GetCreationTime(path);
            item.DateModified = Directory.GetLastAccessTime(path);

            ItemResolveEventArgs args = new ItemResolveEventArgs();
            args.File = new LazyFileInfo() { Path = path };
            args.FileSystemChildren = ConvertFileSystemEntries(Directory.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly));

            await Kernel.Instance.ExecuteMetadataProviders(item, args).ConfigureAwait(false);

            return item;
        }

        private LazyFileInfo[] ConvertFileSystemEntries(string[] files)
        {
            LazyFileInfo[] items = new LazyFileInfo[files.Length];

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];

                items[i] = new LazyFileInfo() { Path = file };
            }

            return items;
        }
    }
}
