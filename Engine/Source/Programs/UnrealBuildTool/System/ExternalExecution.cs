// Copyright 1998-2015 Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace UnrealBuildTool
{
	// This enum has to be compatible with the one defined in the
	// UE4\Engine\Source\Runtime\Core\Public\Misc\ComplilationResult.h 
	// to keep communication between UHT, UBT and Editor compiling
	// processes valid.
	public enum ECompilationResult
	{
		/** Compilation succeeded */
		Succeeded = 0,
		/** Build was canceled, this is used on the engine side only */
		Canceled = 1,
		/** All targets were up to date, used only with -canskiplink */
		UpToDate = 2,
		/** The process has most likely crashed. This is what UE returns in case of an assert */
		CrashOrAssert = 3,
		/** Compilation failed because generated code changed which was not supported */
		FailedDueToHeaderChange = 4,
		/** Compilation failed due to compilation errors */
		OtherCompilationError = 5,
		/** Compilation is not supported in the current build */
		Unsupported,
		/** Unknown error */
		Unknown
	}
	public static class CompilationResultExtensions
	{
		public static bool Succeeded(this ECompilationResult Result)
		{
			return Result == ECompilationResult.Succeeded || Result == ECompilationResult.UpToDate;
		}
	}

	/** Information about a module that needs to be passed to UnrealHeaderTool for code generation */
	[Serializable]
	public class UHTModuleInfo : ISerializable
	{
		/** Module name */
		public string ModuleName;

		/** Module base directory */
		public string ModuleDirectory;

		/** Module type */
		public string ModuleType;

		/** Public UObject headers found in the Classes directory (legacy) */
		public List<FileItem> PublicUObjectClassesHeaders;

		/** Public headers with UObjects */
		public List<FileItem> PublicUObjectHeaders;

		/** Private headers with UObjects */
		public List<FileItem> PrivateUObjectHeaders;

		/** Module PCH absolute path */
		public string PCH;

		/** Base (i.e. extensionless) path+filename of the .generated files */
		public string GeneratedCPPFilenameBase;

		/** Version of code generated by UHT */
		public EGeneratedCodeVersion GeneratedCodeVersion;

		public UHTModuleInfo()
		{
		}

		public UHTModuleInfo(SerializationInfo Info, StreamingContext Context)
		{
			ModuleName                  = Info.GetString("mn");
			ModuleDirectory             = Info.GetString("md");
			ModuleType                  = Info.GetString("mt");
			PublicUObjectClassesHeaders = (List<FileItem>)Info.GetValue("cl", typeof(List<FileItem>));
			PublicUObjectHeaders        = (List<FileItem>)Info.GetValue("pu", typeof(List<FileItem>));
			PrivateUObjectHeaders       = (List<FileItem>)Info.GetValue("pr", typeof(List<FileItem>));
			PCH                         = Info.GetString("pc");
			GeneratedCPPFilenameBase    = Info.GetString("ge");
			GeneratedCodeVersion        = (EGeneratedCodeVersion)Info.GetInt32("gv");
		}

		public void GetObjectData(SerializationInfo Info, StreamingContext Context)
		{
			Info.AddValue("mn", ModuleName);
			Info.AddValue("md", ModuleDirectory);
			Info.AddValue("mt", ModuleType);
			Info.AddValue("cl", PublicUObjectClassesHeaders);
			Info.AddValue("pu", PublicUObjectHeaders);
			Info.AddValue("pr", PrivateUObjectHeaders);
			Info.AddValue("pc", PCH);
			Info.AddValue("ge", GeneratedCPPFilenameBase);
			Info.AddValue("gv", (int)GeneratedCodeVersion);
		}

		public override string ToString()
		{
			return ModuleName;
		}
	}

	//
	// This MUST be kept in sync with EGeneratedBodyVersion enum and 
	// ToGeneratedBodyVersion function in UHT defined in GeneratedCodeVersion.h.
	//
	public enum EGeneratedCodeVersion
	{
		None,
		V1,
		V2,
		VLatest = V2
	};

	public struct UHTManifest
	{
		public struct Module
		{
			public string       Name;
			public string		ModuleType;
			public string       BaseDirectory;
			public string       IncludeBase;     // The include path which all UHT-generated includes should be relative to
			public string       OutputDirectory;
			public List<string> ClassesHeaders;
			public List<string> PublicHeaders;
			public List<string> PrivateHeaders;
			public string       PCH;
			public string       GeneratedCPPFilenameBase;
			public bool         SaveExportedHeaders;
			public EGeneratedCodeVersion UHTGeneratedCodeVersion;

			public override string ToString()
			{
				return Name;
			}
		}

		public UHTManifest(UEBuildTarget Target, string InRootLocalPath, string InRootBuildPath, IEnumerable<UHTModuleInfo> ModuleInfo)
		{
			IsGameTarget  = TargetRules.IsGameType(Target.TargetType);
			RootLocalPath = InRootLocalPath;
			RootBuildPath = InRootBuildPath;
			TargetName    = Target.GetTargetName();

			Modules = ModuleInfo.Select(Info => new Module
			{
				Name                     = Info.ModuleName,
				ModuleType				 = Info.ModuleType,
				BaseDirectory            = Info.ModuleDirectory,
				IncludeBase              = Info.ModuleDirectory,
				OutputDirectory          = Path.GetDirectoryName( Info.GeneratedCPPFilenameBase ),
				ClassesHeaders           = Info.PublicUObjectClassesHeaders.Select((Header) => Header.AbsolutePath).ToList(),
				PublicHeaders            = Info.PublicUObjectHeaders       .Select((Header) => Header.AbsolutePath).ToList(),
				PrivateHeaders           = Info.PrivateUObjectHeaders      .Select((Header) => Header.AbsolutePath).ToList(),
				PCH                      = Info.PCH,
				GeneratedCPPFilenameBase = Info.GeneratedCPPFilenameBase,
				SaveExportedHeaders      = !UnrealBuildTool.IsEngineInstalled() || !Utils.IsFileUnderDirectory(Info.ModuleDirectory, BuildConfiguration.RelativeEnginePath),
				UHTGeneratedCodeVersion = Info.GeneratedCodeVersion,
			}).ToList();
		}

		public bool         IsGameTarget;     // True if the current target is a game target
		public string       RootLocalPath;    // The engine path on the local machine
		public string       RootBuildPath;    // The engine path on the build machine, if different (e.g. Mac/iOS builds)
		public string       TargetName;       // Name of the target currently being compiled
		public List<Module> Modules;
	}


	/**
	 * This handles all running of the UnrealHeaderTool
	 */
	public class ExternalExecution
	{
		/// <summary>
		/// Generates a UHTModuleInfo for a particular named module under a directory.
		/// </summary>
		/// <returns>
		public static UHTModuleInfo CreateUHTModuleInfo(IEnumerable<string> HeaderFilenames, UEBuildTarget Target, string ModuleName, string ModuleDirectory, UEBuildModuleType ModuleType)
		{
			var ClassesFolder = Path.Combine(ModuleDirectory, "Classes");
			var PublicFolder  = Path.Combine(ModuleDirectory, "Public");
			var BuildPlatform = UEBuildPlatform.GetBuildPlatform(Target.Platform);

			var AllClassesHeaders     = new List<FileItem>();
			var PublicUObjectHeaders  = new List<FileItem>();
			var PrivateUObjectHeaders = new List<FileItem>();

			foreach (var Header in HeaderFilenames)
			{
				// Check to see if we know anything about this file.  If we have up-to-date cached information about whether it has
				// UObjects or not, we can skip doing a test here.
				var UObjectHeaderFileItem = FileItem.GetExistingItemByPath( Header );

				if (CPPEnvironment.DoesFileContainUObjects(UObjectHeaderFileItem.AbsolutePath))
				{
					if (UObjectHeaderFileItem.AbsolutePath.StartsWith(ClassesFolder))
					{
						AllClassesHeaders.Add(UObjectHeaderFileItem);
					}
					else if (UObjectHeaderFileItem.AbsolutePath.StartsWith(PublicFolder))
					{
						PublicUObjectHeaders.Add(UObjectHeaderFileItem);
					}
					else
					{
						PrivateUObjectHeaders.Add(UObjectHeaderFileItem);
					}
				}
			}

			var Result = new UHTModuleInfo
			{
				ModuleName                  = ModuleName,
				ModuleDirectory             = ModuleDirectory,
				ModuleType                  = ModuleType.ToString(),
				PublicUObjectClassesHeaders = AllClassesHeaders,
				PublicUObjectHeaders        = PublicUObjectHeaders,
				PrivateUObjectHeaders       = PrivateUObjectHeaders,
				GeneratedCodeVersion        = Target.Rules.GetGeneratedCodeVersion()
			};

			return Result;
		}

		static ExternalExecution()
		{
		}

		/// <summary>
		/// Gets UnrealHeaderTool.exe path. Does not care if UnrealheaderTool was build as a monolithic exe or not.
		/// </summary>
		static string GetHeaderToolPath()
		{
			UnrealTargetPlatform Platform = BuildHostPlatform.Current.Platform;
			string ExeExtension = UEBuildPlatform.GetBuildPlatform(Platform).GetBinaryExtension(UEBuildBinaryType.Executable);
			string HeaderToolExeName = "UnrealHeaderTool";
			string HeaderToolPath = Path.Combine("..", "Binaries", Platform.ToString(), HeaderToolExeName + ExeExtension);
			return HeaderToolPath;
		}

		/// <summary>
		/// Gets the latest write time of any of the UnrealHeaderTool binaries (including DLLs and Plugins) or DateTime.MaxValue if UnrealHeaderTool does not exist
		/// </summary>
		/// <returns>
		/// Latest timestamp of UHT binaries or DateTime.MaxValue if UnrealHeaderTool is out of date and needs to be rebuilt.
		/// </returns>
		static bool GetHeaderToolTimestamp(out DateTime Timestamp)
		{
			using (var TimestampTimer = new ScopedTimer("GetHeaderToolTimestamp"))
			{
				// Try to read the receipt for UHT.
				string ReceiptPath = TargetReceipt.GetDefaultPath(BuildConfiguration.RelativeEnginePath, "UnrealHeaderTool", BuildHostPlatform.Current.Platform, UnrealTargetConfiguration.Development, null);
				if(!File.Exists(ReceiptPath))
				{
					Timestamp = DateTime.MaxValue;
					return false;
				}

				TargetReceipt Receipt;
				try
				{
					Receipt = TargetReceipt.Read(ReceiptPath);
					if(Receipt == null)
					{
						Timestamp = DateTime.MaxValue;
						return false;
					}
				}
				catch(Exception)
				{
					Timestamp = DateTime.MaxValue;
					return false;
				}
				Receipt.ExpandPathVariables(BuildConfiguration.RelativeEnginePath, BuildConfiguration.RelativeEnginePath);

				// Check all the binaries exist, and that all the DLLs are built against the right version
				if(!CheckBinariesExist(Receipt) || !CheckDynamicLibaryVersionsMatch(Receipt))
				{
					Timestamp = DateTime.MaxValue;
					return false;
				}

				// Return the timestamp for all the binaries
				Timestamp = GetTimestampFromBinaries(Receipt);
				return true;
			}
		}

		/// <summary>
		/// Checks if all the files in a receipt are present and that all the DLLs are at the same version
		/// </summary>
		/// <returns>
		/// True if all the files are valid.
		/// </returns>
		static bool CheckBinariesExist(TargetReceipt Receipt)
		{
			bool bExist = true;
			foreach(BuildProduct BuildProduct in Receipt.BuildProducts)
			{
				if(BuildProduct.Type == BuildProductType.Executable || BuildProduct.Type == BuildProductType.DynamicLibrary)
				{
					if(!File.Exists(BuildProduct.Path))
					{
						Log.TraceWarning("Missing binary: {0}", BuildProduct.Path);
						bExist = false;
					}
				}
			}
			return bExist;
		}

		/// <summary>
		/// Checks if all the files in a receipt have the same version
		/// </summary>
		/// <returns>
		/// True if all the files are valid.
		/// </returns>
		static bool CheckDynamicLibaryVersionsMatch(TargetReceipt Receipt)
		{
			List<Tuple<string, int>> BinaryVersions = new List<Tuple<string,int>>();
			foreach(BuildProduct BuildProduct in Receipt.BuildProducts)
			{
				if(BuildProduct.Type == BuildProductType.DynamicLibrary)
				{
					int Version = BuildHostPlatform.Current.GetDllApiVersion(BuildProduct.Path);
					BinaryVersions.Add(new Tuple<string,int>(BuildProduct.Path, Version));
				}
			}

			bool bMatch = true;
			if(BinaryVersions.Count > 0 && !BinaryVersions.All(x => x.Item2 == BinaryVersions[0].Item2))
			{
				Log.TraceWarning("Detected mismatch in binary versions:");
				foreach(Tuple<string, int> BinaryVersion in BinaryVersions)
				{
					Log.TraceWarning("  {0} has API version {1}", BinaryVersion.Item1, BinaryVersion.Item2);
					File.Delete(BinaryVersion.Item1);
				}
				bMatch = false;
			}
			return bMatch;
		}

		/// <summary>
		/// Checks if all the files in a receipt are present and that all the DLLs are at the same version
		/// </summary>
		/// <returns>
		/// True if all the files are valid.
		/// </returns>
		static DateTime GetTimestampFromBinaries(TargetReceipt Receipt)
		{
			DateTime LatestWriteTime = DateTime.MinValue;
			foreach(BuildProduct BuildProduct in Receipt.BuildProducts)
			{
				if(BuildProduct.Type == BuildProductType.Executable || BuildProduct.Type == BuildProductType.DynamicLibrary)
				{
					DateTime WriteTime = File.GetLastWriteTime(BuildProduct.Path);
					if(WriteTime > LatestWriteTime)
					{
						LatestWriteTime = WriteTime;
					}
				}
			}
			return LatestWriteTime;
		}

		/// <summary>
		/// Gets the timestamp of CoreUObject.generated.cpp file.
		/// </summary>
		/// <returns>Last write time of CoreUObject.generated.cpp or DateTime.MaxValue if it doesn't exist.</returns>
		private static DateTime GetCoreGeneratedTimestamp(string ModuleName, string ModuleGeneratedCodeDirectory)
		{			
			DateTime Timestamp;
			if( UnrealBuildTool.RunningRocket() )
			{
				// In Rocket, we don't check the timestamps on engine headers.  Default to a very old date.
				Timestamp = DateTime.MinValue;
			}
			else
			{
				string CoreGeneratedFilename = Path.Combine(ModuleGeneratedCodeDirectory, ModuleName + ".generated.cpp");
				if (File.Exists(CoreGeneratedFilename))
				{
					Timestamp = new FileInfo(CoreGeneratedFilename).LastWriteTime;
				}
				else
				{
					// Doesn't exist, so use a 'newer that everything' date to force rebuild headers.
					Timestamp = DateTime.MaxValue; 
				}
			}

			return Timestamp;
		}

		/**
		 * Checks the class header files and determines if generated UObject code files are out of date in comparison.
		 * @param UObjectModules	Modules that we generate headers for
		 * 
		 * @return					True if the code files are out of date
		 * */
		private static bool AreGeneratedCodeFilesOutOfDate(List<UHTModuleInfo> UObjectModules, DateTime HeaderToolTimestamp)
		{
			// Get CoreUObject.generated.cpp timestamp.  If the source files are older than the CoreUObject generated code, we'll
			// need to regenerate code for the module
			DateTime? CoreGeneratedTimestamp = null;
			{ 
				// Find the CoreUObject module
				foreach( var Module in UObjectModules )
				{
					if( Module.ModuleName.Equals( "CoreUObject", StringComparison.InvariantCultureIgnoreCase ) )
					{
						CoreGeneratedTimestamp = GetCoreGeneratedTimestamp(Module.ModuleName, Path.GetDirectoryName( Module.GeneratedCPPFilenameBase ));
						break;
					}
				}
				if( CoreGeneratedTimestamp == null )
				{
					throw new BuildException( "Could not find CoreUObject in list of all UObjectModules" );
				}
			}


			foreach( var Module in UObjectModules )
			{
				// If the engine is installed, skip skip checking timestamps for modules that are under the engine directory
				if (UnrealBuildTool.IsEngineInstalled() && Utils.IsFileUnderDirectory( Module.ModuleDirectory, BuildConfiguration.RelativeEnginePath))
				{
					continue;
				}

				// Make sure we have an existing folder for generated code.  If not, then we definitely need to generate code!
				var GeneratedCodeDirectory = Path.GetDirectoryName( Module.GeneratedCPPFilenameBase );
				var TestDirectory = (FileSystemInfo)new DirectoryInfo(GeneratedCodeDirectory);
				if( !TestDirectory.Exists )
				{
					// Generated code directory is missing entirely!
					Log.TraceVerbose( "UnrealHeaderTool needs to run because no generated code directory was found for module {0}", Module.ModuleName );
					return true;
				}

				// Grab our special "Timestamp" file that we saved after the last set of headers were generated.  This file
				// actually contains the list of source files which contained UObjects, so that we can compare to see if any
				// UObject source files were deleted (or no longer contain UObjects), which means we need to run UHT even
				// if no other source files were outdated
				string TimestampFile = Path.Combine( GeneratedCodeDirectory, @"Timestamp" );
				var SavedTimestampFileInfo = (FileSystemInfo)new FileInfo(TimestampFile);
				if (!SavedTimestampFileInfo.Exists)
				{
					// Timestamp file was missing (possibly deleted/cleaned), so headers are out of date
					Log.TraceVerbose( "UnrealHeaderTool needs to run because UHT Timestamp file did not exist for module {0}", Module.ModuleName );
					return true;
				}

				// Make sure the last UHT run completed after UnrealHeaderTool.exe was compiled last, and after the CoreUObject headers were touched last.
				var SavedTimestamp = SavedTimestampFileInfo.LastWriteTime;
				if( HeaderToolTimestamp > SavedTimestamp || CoreGeneratedTimestamp > SavedTimestamp )
				{
					// Generated code is older than UnrealHeaderTool.exe or CoreUObject headers.  Out of date!
					Log.TraceVerbose( "UnrealHeaderTool needs to run because UnrealHeaderTool.exe or CoreUObject headers are newer than SavedTimestamp for module {0}", Module.ModuleName );
					return true;
				}

				// Iterate over our UObjects headers and figure out if any of them have changed
				var AllUObjectHeaders = new List<FileItem>();
				AllUObjectHeaders.AddRange( Module.PublicUObjectClassesHeaders );
				AllUObjectHeaders.AddRange( Module.PublicUObjectHeaders );
				AllUObjectHeaders.AddRange( Module.PrivateUObjectHeaders );

				// Load up the old timestamp file and check to see if anything has changed
				{
					var UObjectFilesFromPreviousRun = File.ReadAllLines( TimestampFile );
					if( AllUObjectHeaders.Count != UObjectFilesFromPreviousRun.Length )
					{
						Log.TraceVerbose( "UnrealHeaderTool needs to run because there are a different number of UObject source files in module {0}", Module.ModuleName );
						return true;
					}
					for( int FileIndex = 0; FileIndex < AllUObjectHeaders.Count; ++FileIndex )
					{
						if( !UObjectFilesFromPreviousRun[ FileIndex ].Equals( AllUObjectHeaders[ FileIndex ].AbsolutePath, StringComparison.InvariantCultureIgnoreCase ) )
						{
							Log.TraceVerbose( "UnrealHeaderTool needs to run because the set of UObject source files in module {0} has changed", Module.ModuleName );
							return true;
						}
					}
				}

				foreach( var HeaderFile in AllUObjectHeaders )
				{
					var HeaderFileTimestamp = HeaderFile.Info.LastWriteTime;

					// Has the source header changed since we last generated headers successfully?
					if( HeaderFileTimestamp > SavedTimestamp )
					{
						Log.TraceVerbose( "UnrealHeaderTool needs to run because SavedTimestamp is older than HeaderFileTimestamp ({0}) for module {1}", HeaderFile.AbsolutePath, Module.ModuleName );
						return true;
					}

					// When we're running in assembler mode, outdatedness cannot be inferred by checking the directory timestamp
					// of the source headers.  We don't care if source files were added or removed in this mode, because we're only
					// able to process the known UObject headers that are in the Makefile.  If UObject header files are added/removed,
					// we expect the user to re-run GenerateProjectFiles which will force UBTMakefile outdatedness.
					// @todo ubtmake: Possibly, we should never be doing this check these days.
					if( UnrealBuildTool.IsGatheringBuild || !UnrealBuildTool.IsAssemblingBuild )
					{
						// Also check the timestamp on the directory the source file is in.  If the directory timestamp has
						// changed, new source files may have been added or deleted.  We don't know whether the new/deleted
						// files were actually UObject headers, but because we don't know all of the files we processed
						// in the previous run, we need to assume our generated code is out of date if the directory timestamp
						// is newer.
						var HeaderDirectoryTimestamp = new DirectoryInfo( Path.GetDirectoryName( HeaderFile.AbsolutePath ) ).LastWriteTime;
						if( HeaderDirectoryTimestamp > SavedTimestamp )
						{
							Log.TraceVerbose( "UnrealHeaderTool needs to run because the directory containing an existing header ({0}) has changed, and headers may have been added to or deleted from module {1}", HeaderFile.AbsolutePath, Module.ModuleName );
							return true;
						}
					}
				}
			}

			return false;
		}

		/** Updates the intermediate include directory timestamps of all the passed in UObject modules */
		private static void UpdateDirectoryTimestamps(List<UHTModuleInfo> UObjectModules)
		{
			foreach( var Module in UObjectModules )
			{
				string GeneratedCodeDirectory = Path.GetDirectoryName( Module.GeneratedCPPFilenameBase );
				var GeneratedCodeDirectoryInfo = new DirectoryInfo( GeneratedCodeDirectory );

				try
				{
					if (GeneratedCodeDirectoryInfo.Exists)
					{
						// Don't write anything to the engine directory if we're running an installed build
						if(UnrealBuildTool.IsEngineInstalled() && Utils.IsFileUnderDirectory(Module.ModuleDirectory, BuildConfiguration.RelativeEnginePath))
						{
							continue;
						}

						// Touch the include directory since we have technically 'generated' the headers
						// However, the headers might not be touched at all since that would cause the compiler to recompile everything
						// We can't alter the directory timestamp directly, because this may throw exceptions when the directory is
						// open in visual studio or windows explorer, so instead we create a blank file that will change the timestamp for us
						string TimestampFile = GeneratedCodeDirectoryInfo.FullName + Path.DirectorySeparatorChar + @"Timestamp";

						if( !GeneratedCodeDirectoryInfo.Exists )
						{
							GeneratedCodeDirectoryInfo.Create();
						}

						// Save all of the UObject files to a timestamp file.  We'll load these on the next run to see if any new
						// files with UObject classes were deleted, so that we'll know to run UHT even if the timestamps of all
						// of the other source files were unchanged
						{
							var AllUObjectFiles = new List<string>();
							AllUObjectFiles.AddRange( Module.PublicUObjectClassesHeaders.ConvertAll( Item => Item.AbsolutePath ) );
							AllUObjectFiles.AddRange( Module.PublicUObjectHeaders.ConvertAll( Item => Item.AbsolutePath ) );
							AllUObjectFiles.AddRange( Module.PrivateUObjectHeaders.ConvertAll( Item => Item.AbsolutePath ) );
							ResponseFile.Create( TimestampFile, AllUObjectFiles );
						}
					}
				}
				catch (Exception Exception)
				{
					throw new BuildException(Exception, "Couldn't touch header directories: " + Exception.Message);
				}
			}
		}

		/** Run an external exe (and capture the output), given the exe path and the commandline. */
		public static int RunExternalExecutable(string ExePath, string Commandline)
		{
			var ExeInfo = new ProcessStartInfo(ExePath, Commandline);
			Log.TraceVerbose( "RunExternalExecutable {0} {1}", ExePath, Commandline );
			ExeInfo.UseShellExecute = false;
			ExeInfo.RedirectStandardOutput = true;
			using (var GameProcess = Process.Start(ExeInfo))
			{
				GameProcess.BeginOutputReadLine();
				GameProcess.OutputDataReceived += PrintProcessOutputAsync;
				GameProcess.WaitForExit();

				return GameProcess.ExitCode;
			}
		}

		/** Simple function to pipe output asynchronously */
		private static void PrintProcessOutputAsync(object Sender, DataReceivedEventArgs Event)
		{
			// DataReceivedEventHandler is fired with a null string when the output stream is closed.  We don't want to
			// print anything for that event.
			if( !String.IsNullOrEmpty( Event.Data ) )
			{
				Log.TraceInformation( Event.Data );
			}
		}



		/**
		 * Builds and runs the header tool and touches the header directories.
		 * Performs any early outs if headers need no changes, given the UObject modules, tool path, game name, and configuration
		 */
		public static bool ExecuteHeaderToolIfNecessary( UEBuildTarget Target, CPPEnvironment GlobalCompileEnvironment, List<UHTModuleInfo> UObjectModules, string ModuleInfoFileName, ref ECompilationResult UHTResult )
		{
			if(ProgressWriter.bWriteMarkup)
			{
				Log.WriteLine(LogEventType.Console, "@progress push 5%");
			}
			using (ProgressWriter Progress = new ProgressWriter("Generating code...", false))
			{
				// We never want to try to execute the header tool when we're already trying to build it!
				var bIsBuildingUHT = Target.GetTargetName().Equals( "UnrealHeaderTool", StringComparison.InvariantCultureIgnoreCase );

				var BuildPlatform = UEBuildPlatform.GetBuildPlatform(Target.Platform);
				var CppPlatform = BuildPlatform.GetCPPTargetPlatform(Target.Platform);
				var ToolChain = UEToolChain.GetPlatformToolChain(CppPlatform);
				var RootLocalPath  = Path.GetFullPath(ProjectFileGenerator.RootRelativePath);

				// check if UHT is out of date
				DateTime HeaderToolTimestamp = DateTime.MaxValue;
				bool bHaveHeaderTool = !bIsBuildingUHT && GetHeaderToolTimestamp(out HeaderToolTimestamp);

				// ensure the headers are up to date
				bool bUHTNeedsToRun = (UEBuildConfiguration.bForceHeaderGeneration == true || !bHaveHeaderTool || AreGeneratedCodeFilesOutOfDate(UObjectModules, HeaderToolTimestamp));
				if( bUHTNeedsToRun || UnrealBuildTool.IsGatheringBuild )
				{
					// Since code files are definitely out of date, we'll now finish computing information about the UObject modules for UHT.  We
					// want to save this work until we know that UHT actually needs to be run to speed up best-case iteration times.
					if( UnrealBuildTool.IsGatheringBuild )		// In assembler-only mode, PCH info is loaded from our UBTMakefile!
					{
						foreach( var UHTModuleInfo in UObjectModules )
						{
							// Only cache the PCH name if we don't already have one.  When running in 'gather only' mode, this will have already been cached
							if( string.IsNullOrEmpty( UHTModuleInfo.PCH ) )
							{
								UHTModuleInfo.PCH = "";

								// We need to figure out which PCH header this module is including, so that UHT can inject an include statement for it into any .cpp files it is synthesizing
								var DependencyModuleCPP = (UEBuildModuleCPP)Target.GetModuleByName( UHTModuleInfo.ModuleName );
								var ModuleCompileEnvironment = DependencyModuleCPP.CreateModuleCompileEnvironment(GlobalCompileEnvironment);
								DependencyModuleCPP.CachePCHUsageForModuleSourceFiles(ModuleCompileEnvironment);
								if (DependencyModuleCPP.ProcessedDependencies.UniquePCHHeaderFile != null)
								{
									UHTModuleInfo.PCH = DependencyModuleCPP.ProcessedDependencies.UniquePCHHeaderFile.AbsolutePath;
								}
							}
						}
					}
				}

				// @todo ubtmake: Optimization: Ideally we could avoid having to generate this data in the case where UHT doesn't even need to run!  Can't we use the existing copy?  (see below use of Manifest)
				UHTManifest Manifest = new UHTManifest(Target, RootLocalPath, ToolChain.ConvertPath(RootLocalPath + '\\'), UObjectModules);

				if( !bIsBuildingUHT && bUHTNeedsToRun )
				{
					// Always build UnrealHeaderTool if header regeneration is required, unless we're running within a Rocket ecosystem or hot-reloading
					if (UnrealBuildTool.RunningRocket() == false && 
						UEBuildConfiguration.bDoNotBuildUHT == false &&
						UEBuildConfiguration.bHotReloadFromIDE == false &&
						!( bHaveHeaderTool && !UnrealBuildTool.IsGatheringBuild && UnrealBuildTool.IsAssemblingBuild ) )	// If running in "assembler only" mode, we assume UHT is already up to date for much faster iteration!
					{
						// If it is out of date or not there it will be built.
						// If it is there and up to date, it will add 0.8 seconds to the build time.
						Log.TraceInformation("Building UnrealHeaderTool...");

						var UBTArguments = new StringBuilder();

						UBTArguments.Append( "UnrealHeaderTool" );

						// Which desktop platform do we need to compile UHT for?
						UBTArguments.Append(" " + BuildHostPlatform.Current.Platform.ToString());
						// NOTE: We force Development configuration for UHT so that it runs quickly, even when compiling debug
						UBTArguments.Append( " " + UnrealTargetConfiguration.Development.ToString() );

						// NOTE: We disable mutex when launching UBT from within UBT to compile UHT
						UBTArguments.Append( " -NoMutex" );

						if (UnrealBuildTool.CommandLineContains("-noxge"))
						{
							UBTArguments.Append(" -noxge");
						}

						// Propagate command-line option to enable the newer compiler on Windows platform
						if (UnrealBuildTool.CommandLineContains("-2015"))
						{
							UBTArguments.Append(" -2015");
						}
						
						if ( RunExternalExecutable( UnrealBuildTool.GetUBTPath(), UBTArguments.ToString() ) != 0 )
						{ 
							return false;
						}
					}

					Progress.Write(1, 3);

					var ActualTargetName = String.IsNullOrEmpty( Target.GetTargetName() ) ? "UE4" : Target.GetTargetName();
					Log.TraceInformation( "Parsing headers for {0}", ActualTargetName );

					string HeaderToolPath = GetHeaderToolPath();
					if (!File.Exists(HeaderToolPath))
					{
						throw new BuildException( "Unable to generate headers because UnrealHeaderTool binary was not found ({0}).", Path.GetFullPath( HeaderToolPath ) );
					}

					// Disable extensions when serializing to remove the $type fields
					Directory.CreateDirectory(Path.GetDirectoryName(ModuleInfoFileName));
					System.IO.File.WriteAllText(ModuleInfoFileName, fastJSON.JSON.Instance.ToJSON(Manifest, new fastJSON.JSONParameters{ UseExtensions = false }));

					string CmdLine = (UnrealBuildTool.HasUProjectFile()) ? "\"" + UnrealBuildTool.GetUProjectFile() + "\"" : Target.GetTargetName();
					CmdLine += " \"" + ModuleInfoFileName + "\" -LogCmds=\"loginit warning, logexit warning, logdatabase error\"";
					if (UnrealBuildTool.RunningRocket())
					{
						CmdLine += " -rocket -installed";
					}

					if (UEBuildConfiguration.bFailIfGeneratedCodeChanges)
					{
						CmdLine += " -FailIfGeneratedCodeChanges";
					}

					Log.TraceInformation("  Running UnrealHeaderTool {0}", CmdLine);

					Stopwatch s = new Stopwatch();
					s.Start();
					UHTResult = (ECompilationResult) RunExternalExecutable(ExternalExecution.GetHeaderToolPath(), CmdLine);
					s.Stop();

					if (UHTResult != ECompilationResult.Succeeded)
					{
						// On Linux and Mac, the shell will return 128+signal number exit codes if UHT gets a signal (e.g. crashes or is interrupted)
						if ((BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Linux ||
						    BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Mac) &&
						    (int)(UHTResult) >= 128
						    )
						{
							// SIGINT is 2, so 128 + SIGINT is 130
							UHTResult = ((int)(UHTResult) == 130) ? ECompilationResult.Canceled : ECompilationResult.CrashOrAssert;
						}

						Log.TraceInformation("Error: Failed to generate code for {0} - error code: {2} ({1})", ActualTargetName, (int) UHTResult, UHTResult.ToString());
						return false;
					}

					Log.TraceInformation("Reflection code generated for {0} in {1} seconds", ActualTargetName, s.Elapsed.TotalSeconds);
					if( BuildConfiguration.bPrintPerformanceInfo )
					{
						Log.TraceInformation( "UnrealHeaderTool took {1}", ActualTargetName, (double)s.ElapsedMilliseconds/1000.0 );
					}

					// Now that UHT has successfully finished generating code, we need to update all cached FileItems in case their last write time has changed.
					// Otherwise UBT might not detect changes UHT made.
					DateTime StartTime = DateTime.UtcNow;
					FileItem.ResetInfos();
					double ResetDuration = (DateTime.UtcNow - StartTime).TotalSeconds;
					Log.TraceVerbose("FileItem.ResetInfos() duration: {0}s", ResetDuration);
				}
				else
				{
					Log.TraceVerbose( "Generated code is up to date." );
				}

				Progress.Write(2, 3);

				// There will never be generated code if we're building UHT, so this should never be called.
				if (!bIsBuildingUHT)
				{
					// Allow generated code to be sync'd to remote machines if needed. This needs to be done even if UHT did not run because
					// generated headers include other generated headers using absolute paths which in case of building remotely are already
					// the remote machine absolute paths. Because of that parsing headers will not result in finding all includes properly.
					// @todo ubtmake: Need to figure out what this does in the assembler case, and whether we need to run it
					ToolChain.PostCodeGeneration(Manifest);
				}

				// touch the directories
				UpdateDirectoryTimestamps(UObjectModules);

				Progress.Write(3, 3);
			}
			if(ProgressWriter.bWriteMarkup)
			{
				Log.WriteLine(LogEventType.Console, "@progress pop");
			}
			return true;
		}
	}
}
