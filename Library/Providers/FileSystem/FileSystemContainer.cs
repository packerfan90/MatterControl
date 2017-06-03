﻿/*
Copyright (c) 2017, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.PlatformAbstract;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl.CustomWidgets;

namespace MatterHackers.MatterControl.Library
{
	public class FileSystemContainer : WritableContainer
	{
		private string fullPath;

		private FileSystemWatcher directoryWatcher;

		private bool isActiveContainer;
		private bool isDirty;

		private static Regex fileNameNumberMatch = new Regex("\\(\\d+\\)", RegexOptions.Compiled);

		public FileSystemContainer(string path)
		{
			this.fullPath = path;
			this.Name = Path.GetFileName(path);

			this.ChildContainers = new List<ILibraryContainerLink>();
			this.Items = new List<ILibraryItem>();

			if (OsInformation.OperatingSystem == OSType.Windows)
			{
				directoryWatcher = new FileSystemWatcher(path);

				directoryWatcher.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite
					   | NotifyFilters.FileName | NotifyFilters.DirectoryName;
				directoryWatcher.Changed += DirectoryContentsChanged;
				directoryWatcher.Created += DirectoryContentsChanged;
				directoryWatcher.Deleted += DirectoryContentsChanged;
				directoryWatcher.Renamed += DirectoryContentsChanged;

				// TODO: Needed? Observed events firing for file create in subfolders
				//directoryWatcher.IncludeSubdirectories = false;

				// Begin watching.
				directoryWatcher.EnableRaisingEvents = true;
			}

			GetFilesAndCollectionsInCurrentDirectory();
		}

		// Indicates if the new AMF file should use the original file name incremented until no name collision occurs
		public bool UseIncrementedNameDuringTypeChange { get; internal set; }

		public override void Activate()
		{
			this.isActiveContainer = true;

			if (isDirty)
			{
				// Requires reload
				GetFilesAndCollectionsInCurrentDirectory();
			}
			base.Activate();
		}

		public override void Deactivate()
		{
			this.isActiveContainer = false;
			base.Deactivate();
		}

		private string keywordFilter = "";
		public override string KeywordFilter
		{
			get
			{
				return keywordFilter;
			}

			set
			{
				if (keywordFilter != value)
				{
					keywordFilter = value;

					if (isActiveContainer)
					{
						GetFilesAndCollectionsInCurrentDirectory(true);
					}
				}
			}
		}

		public override void Dispose()
		{
			if (directoryWatcher != null)
			{
				directoryWatcher.EnableRaisingEvents = false;

				directoryWatcher.Changed -= DirectoryContentsChanged;
				directoryWatcher.Created -= DirectoryContentsChanged;
				directoryWatcher.Deleted -= DirectoryContentsChanged;
				directoryWatcher.Renamed -= DirectoryContentsChanged;
			}
		}

		private void DirectoryContentsChanged(object sender, EventArgs e)
		{
			// Flag for reload
			isDirty = true;

			// Only refresh content if we're the active container
			if (isActiveContainer)
			{
				GetFilesAndCollectionsInCurrentDirectory();
			}
		}

		private async void GetFilesAndCollectionsInCurrentDirectory(bool recursive = false)
		{
			SearchOption searchDepth = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

			await Task.Run(() =>
			{
				try
				{
					string filter = this.KeywordFilter.Trim();

					var allFiles = Directory.GetFiles(fullPath, "*.*", searchDepth);

					var zipFiles = allFiles.Where(f => Path.GetExtension(f).IndexOf(".zip", StringComparison.OrdinalIgnoreCase) != -1);

					var nonZipFiles = allFiles.Except(zipFiles);

					List<ILibraryContainerLink> containers;
					if (filter == "")
					{
						var directories = Directory.GetDirectories(fullPath, "*.*", searchDepth).Select(p => new ContainerLink(p)).ToList<ILibraryContainerLink>();
						containers = directories.Concat(zipFiles.Select(f => new LocalZipContainerLink(f))).OrderBy(d => d.Name).ToList();
					}
					else
					{
						containers = new List<ILibraryContainerLink>();
					}

					var matchedFiles = (filter == "") ? nonZipFiles : nonZipFiles.Where(filePath =>
					{
						string fileName = Path.GetFileName(filePath);
						return FileNameContainsFilter(filePath, filter)
							&& ApplicationController.Instance.Library.IsContentFileType(fileName);
					});
					
					UiThread.RunOnIdle(() =>
					{
						// Matched containers
						this.ChildContainers = containers;

						// Matched files projected onto FileSystemFileItem
						this.Items = matchedFiles.OrderBy(f => f).Select(f => new FileSystemFileItem(f)).ToList<ILibraryItem>();

						this.isDirty = false;

						this.OnReloaded();
					});
				}
				catch (Exception ex)
				{
					this.ChildContainers = new List<ILibraryContainerLink>();
					this.Items = new List<ILibraryItem>()
					{
						new MessageItem("Error loading container - " + ex.Message)
					};
				}
			});
		}

		private bool FileNameContainsFilter(string filename, string filter)
		{
			string nameWithSpaces = Path.GetFileNameWithoutExtension(filename.Replace('_', ' '));

			// Split the filter on word boundaries and determine if all terms in the given file name
			foreach (string word in filter.Split(' '))
			{
				if (nameWithSpaces.IndexOf(word, StringComparison.OrdinalIgnoreCase) == -1)
				{
					return false;
				}
			}

			return true;
		}

		#region Container Actions

		private string GetNonCollidingName(string fileName)
		{
			string incrementedFilePath;
			string fileExtension = Path.GetExtension(fileName);

			// Switching from .stl, .obj or similar to AMF. Save the file and update the
			// the filename with an incremented (n) value to reflect the extension change in the UI 
			fileName = Path.GetFileNameWithoutExtension(fileName);

			// Drop bracketed number sections from our source filename to ensure we don't generate something like "file (1) (1).amf"
			if (fileName.Contains("("))
			{
				fileName = fileNameNumberMatch.Replace(fileName, "").Trim();
			}

			// Generate and search for an incremented file name until no match is found at the target directory
			int foundCount = 1;
			do
			{
				incrementedFilePath = Path.Combine(this.fullPath, $"{fileName} ({foundCount++}){fileExtension}");

				// Continue incrementing while any matching file exists
			} while (Directory.GetFiles(incrementedFilePath).Any());

			return incrementedFilePath;
		}

		public async override void Add(IEnumerable<ILibraryItem> items)
		{
			if (!items.Any())
			{
				return;
			}

			directoryWatcher.EnableRaisingEvents = false;
			this.isDirty = true;

			Directory.CreateDirectory(this.fullPath);

			await Task.Run(async () =>
			{
				/*			string directoryPath = Path.Combine(this.fullPath, container.Name);
			if (!Directory.Exists(directoryPath))
			{
				Directory.CreateDirectory(directoryPath);
				//GetFilesAndCollectionsInCurrentDirectory();
			}*/

				foreach (var item in items.OfType<ILibraryContentStream>())
				{
					string filePath = Path.Combine(this.fullPath, item.FileName);

					try
					{
						if (File.Exists(filePath))
						{
							filePath = GetNonCollidingName(Path.GetFileName(filePath));
						}

						using (var outputStream = File.OpenWrite(filePath))
						using (var contentStream = await item.GetContentStream(null))
						{
							contentStream.Stream.CopyTo(outputStream);
						}

						this.Items.Add(new FileSystemFileItem(filePath));
					}
					catch (Exception ex)
					{
						UiThread.RunOnIdle(() =>
						{
							ApplicationController.Instance.Terminal.Log($"Error adding file: {filePath}\r\n{ex.Message}");
						});
					}
				}
			});

			directoryWatcher.EnableRaisingEvents = false;

			this.OnReloaded();
		}

		public override void Remove(IEnumerable<ILibraryItem> items)
		{
			directoryWatcher.EnableRaisingEvents = false;
			this.isDirty = true;

			/*// TODO: Do we really want to be doing this in App? Seems too risky to take on. Imagine the Desktop being is added as a library folder?
			var fileSystemContainer = container as FileSystemContainer;
			if (fileSystemContainer != null
				&& Directory.Exists(fileSystemContainer.fullPath))
			{
				Directory.Delete(fileSystemContainer.fullPath, true);

				await Task.Delay(150);

				GetFilesAndCollectionsInCurrentDirectory();
			}*/

			foreach(var item in items.OfType<ILibraryContentStream>())
			{
				string filePath = Path.Combine(this.fullPath, item.FileName);

				this.Items.RemoveAll(i => 
				{
					// Return true (and thus Remove) any item that is FileSystemFileItem and has the given path
					var fileItem = i as FileSystemFileItem;
					return fileItem != null
						&& fileItem.Path == filePath;
				});

				// TODO: Platform specific delete with undo? Recycle Bin/Trash/etc...
				File.Delete(filePath);
			}

			directoryWatcher.EnableRaisingEvents = true;
			this.OnReloaded();
		}

		public override void Rename(ILibraryItem item, string revisedName)
		{
			/*
			 * var fileSystemContainer = container as FileSystemContainer;
			if (fileSystemContainer != null
				&& Directory.Exists(fileSystemContainer.fullPath))
			{
				string destPath = Path.Combine(Path.GetDirectoryName(fileSystemContainer.fullPath), revisedName);
				Directory.Move(fileSystemContainer.fullPath, destPath);

				await Task.Delay(150);

				GetFilesAndCollectionsInCurrentDirectory();
			}*/

			/*
			string sourceFile = Path.Combine(rootPath, currentDirectoryFiles[itemIndexToRename]);
			if (File.Exists(sourceFile))
			{
				string extension = Path.GetExtension(sourceFile);
				string destFile = Path.Combine(Path.GetDirectoryName(sourceFile), newName);
				destFile = Path.ChangeExtension(destFile, extension);
				File.Move(sourceFile, destFile);
				Stopwatch time = Stopwatch.StartNew();
				// Wait for up to some amount of time for the directory to be gone.
				while (File.Exists(destFile)
					&& time.ElapsedMilliseconds < 100)
				{
					Thread.Sleep(1); // make sure we are not eating all the cpu time.
				}
				//				GetFilesAndCollectionsInCurrentDirectory();
			}
			*/
		}

		#endregion

		public class ContainerLink : FileSystemItem, ILibraryContainerLink
		{
			public ContainerLink(string path)
				: base(path)
			{
			}

			public bool UseIncrementedNameDuringTypeChange { get; set; }

			public Task<ILibraryContainer> GetContainer(ReportProgressRatio reportProgress)
			{
				return Task.FromResult<ILibraryContainer>(
					new FileSystemContainer(this.Path)
					{
						UseIncrementedNameDuringTypeChange = this.UseIncrementedNameDuringTypeChange
					});
			}
		}
	}
}