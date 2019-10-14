#tool nuget:?package=NUnit.ConsoleRunner
#addin nuget:?package=Cake.Git

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Debug");

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

// Define directories.
var workingDir = MakeAbsolute(Directory("./"));
string artifactsDirName = "Artifacts";
string testResultsDirName = "TestResults";
string netCoreFramework = "netcoreapp3.0";
string serverProjectPath = "./src/Jackett.Server/Jackett.Server.csproj";
string updaterProjectPath = "./src/Jackett.Updater/Jackett.Updater.csproj";

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Info")
	.Does(() =>
	{
		Information(@"Jackett Cake build script starting...");
		Information(@"Requires InnoSetup and C:\msys64 to be present for packaging (Pre-installed on AppVeyor) on Windows");
		Information(@"Working directory is: " + workingDir);

		if (IsRunningOnWindows())
		{
			Information("Platform is Windows");
		}
		else
		{
			Information("Platform is Linux, Windows builds will be skipped");
		}
	});

Task("Clean")
	.IsDependentOn("Info")
	.Does(() =>
	{
		CleanDirectories("./src/**/obj");
		CleanDirectories("./src/**/bin");
		CleanDirectories("./BuildOutput");
		CleanDirectories("./" + artifactsDirName);
		CleanDirectories("./" + testResultsDirName);

		CreateDirectory("./" + artifactsDirName);

		Information("Clean completed");
	});

Task("Build-Full-Framework")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		NuGetRestore("./src/Jackett.sln");

		var buildSettings = new MSBuildSettings()
                .SetConfiguration(configuration)
                .UseToolVersion(MSBuildToolVersion.VS2019);
		
		MSBuild("./src/Jackett.sln", buildSettings);
	});

Task("Run-Unit-Tests")
	.IsDependentOn("Build-Full-Framework")
	.Does(() =>
	{
		CreateDirectory("./" + testResultsDirName);
		var resultsFile = $"./{testResultsDirName}/JackettTestResult.xml";

		NUnit3("./src/**/bin/" + configuration + "/**/*.Test.dll", new NUnit3Settings
		{
			Results = new[] { new NUnit3Result { FileName = resultsFile } }
		});

		if (AppVeyor.IsRunningOnAppVeyor && IsRunningOnWindows())
		{
			AppVeyor.UploadTestResults(resultsFile, AppVeyorTestResultsType.NUnit3);
		}
	});

Task("Package-Windows-Full-Framework")
	.IsDependentOn("Run-Unit-Tests")
	.Does(() =>
	{
		string buildOutputPath = "./BuildOutput/net461/win7-x86/Jackett";
		
		DotNetCorePublish(serverProjectPath, "net461", "win7-x86", buildOutputPath);

		CopyFiles("./src/Jackett.Service/bin/" + configuration + "/JackettService.*", buildOutputPath);
		CopyFiles("./src/Jackett.Tray/bin/" + configuration + "/JackettTray.*", buildOutputPath);
		CopyFiles("./src/Jackett.Updater/bin/" + configuration + "/net461" + "/JackettUpdater.*", buildOutputPath);  //builds against multiple frameworks

		Zip("./BuildOutput/net461/win7-x86", $"./{artifactsDirName}/Jackett.Binaries.Windows.zip");

		//InnoSetup
		string sourceFolder = MakeAbsolute(Directory(buildOutputPath)).ToString();

		InnoSetupSettings settings = new InnoSetupSettings();
		settings.OutputDirectory = workingDir + "/" + artifactsDirName;
		//Can remove below line once Cake is updated for InnoSetup 6 - https://github.com/cake-build/cake/pull/2565
		settings.ToolPath = @"C:\Program Files (x86)\Inno Setup 6\ISCC.exe";
 		settings.Defines = new Dictionary<string, string>
			{
				{ "MyFileForVersion", sourceFolder + "/Jackett.Common.dll" },
				{ "MySourceFolder",  sourceFolder },
				{ "MyOutputFilename",  "Jackett.Installer.Windows" },
			};

		InnoSetup("./Installer.iss", settings);
	});

Task("Package-Mono-Full-Framework")
	.IsDependentOn("Run-Unit-Tests")
	.Does(() =>
	{
		string buildOutputPath = "./BuildOutput/net461/linux-x64/Jackett";

		DotNetCorePublish(serverProjectPath, "net461", "linux-x64", buildOutputPath);

		CopyFiles("./src/Jackett.Updater/bin/" + configuration + "/net461" + "/JackettUpdater.*", buildOutputPath);  //builds against multiple frameworks

		CopyFileToDirectory("./install_service_systemd_mono.sh", buildOutputPath);
		CopyFileToDirectory("./Upstart.config", buildOutputPath);

		//There is an issue with Mono 5.8 (fixed in Mono 5.12) where its expecting to use its own patched version of System.Net.Http.dll, instead of the version supplied in folder
		//https://github.com/dotnet/corefx/issues/19914
		//https://bugzilla.xamarin.com/show_bug.cgi?id=60315
		//The workaround is to delete System.Net.Http.dll and patch the .exe.config file

		DeleteFile(buildOutputPath + "/System.Net.Http.dll");

		var configFile = File(buildOutputPath + "/JackettConsole.exe.config");
		XmlPoke(configFile, "configuration/runtime/*[name()='assemblyBinding']/*[name()='dependentAssembly']/*[name()='assemblyIdentity'][@name='System.Net.Http']/../*[name()='bindingRedirect']/@newVersion", "4.0.0.0");

		//Mono on FreeBSD doesn't like the bundled System.Runtime.InteropServices.RuntimeInformation
		//https://github.com/dotnet/corefx/issues/23989
		//https://github.com/Jackett/Jackett/issues/3547

		DeleteFile(buildOutputPath + "/System.Runtime.InteropServices.RuntimeInformation.dll");

		InstallMsysTar();
		Gzip("./BuildOutput/net461/linux-x64", $"./{artifactsDirName}", "Jackett", "Jackett.Binaries.Mono.tar.gz");
	});

Task("Package-DotNetCore-macOS")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		string runtimeId = "osx-x64";
		string buildOutputPath = $"./BuildOutput/{netCoreFramework}/{runtimeId}/Jackett";
		string updaterOutputPath = buildOutputPath + "/Updater";

		DotNetCorePublish(serverProjectPath, netCoreFramework, runtimeId, buildOutputPath);

		DotNetCorePublish(updaterProjectPath, netCoreFramework, runtimeId, updaterOutputPath);
		CopyFiles(updaterOutputPath + "/JackettUpdater*", buildOutputPath);
		DeleteDirectory(updaterOutputPath, new DeleteDirectorySettings {Recursive = true, Force = true});

		CopyFileToDirectory("./install_service_macos", buildOutputPath);

		Gzip($"./BuildOutput/{netCoreFramework}/{runtimeId}", $"./{artifactsDirName}", "Jackett", "Jackett.Binaries.macOS.tar.gz");
	});

Task("Package-DotNetCore-LinuxAMDx64")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		string runtimeId = "linux-x64";
		string buildOutputPath = $"./BuildOutput/{netCoreFramework}/{runtimeId}/Jackett";
		string updaterOutputPath = buildOutputPath + "/Updater";

		DotNetCorePublish(serverProjectPath, netCoreFramework, runtimeId, buildOutputPath);

		DotNetCorePublish(updaterProjectPath, netCoreFramework, runtimeId, updaterOutputPath);
		CopyFiles(updaterOutputPath + "/JackettUpdater*", buildOutputPath);
		DeleteDirectory(updaterOutputPath, new DeleteDirectorySettings {Recursive = true, Force = true});

		CopyFileToDirectory("./install_service_systemd.sh", buildOutputPath);

		Gzip($"./BuildOutput/{netCoreFramework}/{runtimeId}", $"./{artifactsDirName}", "Jackett", "Jackett.Binaries.LinuxAMDx64.tar.gz");
	});

Task("Package-DotNetCore-LinuxARM32")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		string runtimeId = "linux-arm";
		string buildOutputPath = $"./BuildOutput/{netCoreFramework}/{runtimeId}/Jackett";
		string updaterOutputPath = buildOutputPath + "/Updater";

		DotNetCorePublish(serverProjectPath, netCoreFramework, runtimeId, buildOutputPath);

		DotNetCorePublish(updaterProjectPath, netCoreFramework, runtimeId, updaterOutputPath);
		CopyFiles(updaterOutputPath + "/JackettUpdater*", buildOutputPath);
		DeleteDirectory(updaterOutputPath, new DeleteDirectorySettings {Recursive = true, Force = true});

		CopyFileToDirectory("./install_service_systemd.sh", buildOutputPath);

		Gzip($"./BuildOutput/{netCoreFramework}/{runtimeId}", $"./{artifactsDirName}", "Jackett", "Jackett.Binaries.LinuxARM32.tar.gz");
	});

Task("Package-DotNetCore-LinuxARM64")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		string runtimeId = "linux-arm64";
		string buildOutputPath = $"./BuildOutput/{netCoreFramework}/{runtimeId}/Jackett";
		string updaterOutputPath = buildOutputPath + "/Updater";

		DotNetCorePublish(serverProjectPath, netCoreFramework, runtimeId, buildOutputPath);

		DotNetCorePublish(updaterProjectPath, netCoreFramework, runtimeId, updaterOutputPath);
		CopyFiles(updaterOutputPath + "/JackettUpdater*", buildOutputPath);
		DeleteDirectory(updaterOutputPath, new DeleteDirectorySettings {Recursive = true, Force = true});

		CopyFileToDirectory("./install_service_systemd.sh", buildOutputPath);

		Gzip($"./BuildOutput/{netCoreFramework}/{runtimeId}", $"./{artifactsDirName}", "Jackett", "Jackett.Binaries.LinuxARM64.tar.gz");
	});

Task("Appveyor-Push-Artifacts")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		if (AppVeyor.IsRunningOnAppVeyor)
		{
			foreach (var file in GetFiles(workingDir + $"/{artifactsDirName}/*"))
			{
				AppVeyor.UploadArtifact(file.FullPath);
			}
		}
		else
		{
			Information(@"Skipping artifact push as not running in AppVeyor Windows Environment");
		}
	});

Task("Release-Notes")
	.IsDependentOn("Clean")
	.Does(() =>
	{
		string latestTag = GitDescribe(".", false, GitDescribeStrategy.Tags, 0);
		Information($"Latest tag is: {latestTag}" + Environment.NewLine);

		List<GitCommit> relevantCommits = new List<GitCommit>();

		var commitCollection = GitLog("./", 50);

		foreach(GitCommit commit in commitCollection)
		{
			var commitTag = GitDescribe(".", commit.Sha, false, GitDescribeStrategy.Tags, 0);

			if (commitTag == latestTag)
			{
				relevantCommits.Add(commit);
			}
			else
			{
				break;
			}
		}

		relevantCommits = relevantCommits.AsEnumerable().Reverse().Skip(1).ToList();

		if (relevantCommits.Count() > 0)
		{
			List<string> notesList = new List<string>();
				
			foreach(GitCommit commit in relevantCommits)
			{
				notesList.Add($"{commit.MessageShort} (Thank you @{commit.Author.Name})");
			}

			string buildNote = String.Join(Environment.NewLine, notesList);
			Information(buildNote);

			System.IO.File.WriteAllLines(workingDir + "/BuildOutput/ReleaseNotes.txt", notesList.ToArray());
		}
		else
		{
			Information($"No commit messages found to create release notes");
		}

	});

Task("Windows-Environment-Dev")
	.IsDependentOn("Package-Windows-Full-Framework")
	.IsDependentOn("Package-Mono-Full-Framework")
	.IsDependentOn("Package-DotNetCore-macOS")
	.IsDependentOn("Package-DotNetCore-LinuxAMDx64")
	.IsDependentOn("Package-DotNetCore-LinuxARM32")
	.IsDependentOn("Package-DotNetCore-LinuxARM64")
	.IsDependentOn("Appveyor-Push-Artifacts")
	.IsDependentOn("Release-Notes")
	.Does(() =>
	{
		Information("Windows-Environment Task Completed");
	});

Task("Windows-Environment-Appveyor")
	.IsDependentOn("Package-Windows-Full-Framework")
	.IsDependentOn("Package-Mono-Full-Framework")
	.IsDependentOn("Appveyor-Push-Artifacts")
	.IsDependentOn("Release-Notes")
	.Does(() =>
	{
		Information("Windows-Environment Task Completed");
	});

Task("Linux-Environment")
	.IsDependentOn("Package-DotNetCore-macOS")
	.IsDependentOn("Package-DotNetCore-LinuxAMDx64")
	.IsDependentOn("Package-DotNetCore-LinuxARM32")
	.IsDependentOn("Package-DotNetCore-LinuxARM64")
	.IsDependentOn("Appveyor-Push-Artifacts")
	.IsDependentOn("Release-Notes")
	.Does(() =>
	{
		Information("Linux-Environment Task Completed");
	});


private void RunMsysCommand(string utility, string utilityArguments)
{
	var msysDir = @"C:\msys64\usr\bin\";
	var utilityProcess = msysDir + utility + ".exe";

	Information("MSYS2 Utility: " + utility);
	Information("MSYS2 Directory: " + msysDir);
	Information("Utility Location: " + utilityProcess);
	Information("Utility Arguments: " + utilityArguments);

	IEnumerable<string> redirectedStandardOutput;
	IEnumerable<string> redirectedErrorOutput;
	var exitCodeWithArgument =
		StartProcess(
			utilityProcess,
			new ProcessSettings {
				Arguments = utilityArguments,
				WorkingDirectory = msysDir,
				RedirectStandardOutput = true
			},
			out redirectedStandardOutput,
			out redirectedErrorOutput
		);

	Information(utility + " output:" + Environment.NewLine + string.Join(Environment.NewLine, redirectedStandardOutput.ToArray()));

	// Throw exception if anything was written to the standard error.
	if (redirectedErrorOutput != null && redirectedErrorOutput.Any())
	{
		throw new Exception(
			string.Format(
				utility + " Errors ocurred: {0}",
				string.Join(", ", redirectedErrorOutput)));
	}

	Information(utility + " Exit code: {0}", exitCodeWithArgument);
}

private string RelativeWinPathToFullPath(string relativePath)
{
	return (workingDir + relativePath.TrimStart('.'));
}

private void RunLinuxCommand(string file, string arg)
{
	var startInfo = new System.Diagnostics.ProcessStartInfo()
	{
		Arguments = arg,
		FileName = file,
		UseShellExecute = true
	};

	var process = System.Diagnostics.Process.Start(startInfo);
	process.WaitForExit();
}

private void Gzip(string sourceFolder, string outputDirectory, string tarCdirectoryOption, string outputFileName)
{
	var tarFileName = outputFileName.Remove(outputFileName.Length - 3, 3);
	
	if (IsRunningOnWindows())
	{
		var fullSourcePath = RelativeWinPathToFullPath(sourceFolder);
		var tarArguments = @"--force-local -cvf " + fullSourcePath + "/" + tarFileName + " -C " + fullSourcePath + $" {tarCdirectoryOption} --mode ='755'";
		var gzipArguments = @"-k " + fullSourcePath + "/" + tarFileName;

		RunMsysCommand("tar", tarArguments);
		RunMsysCommand("gzip", gzipArguments);
		MoveFile($"{sourceFolder}/{tarFileName}.gz", $"{outputDirectory}/{tarFileName}.gz");
	}
	else
	{
		RunLinuxCommand("find",  MakeAbsolute(Directory(sourceFolder)) + @" -type d -exec chmod 755 {} \;");
		RunLinuxCommand("find",  MakeAbsolute(Directory(sourceFolder)) + @" -type f -exec chmod 644 {} \;");
		RunLinuxCommand("chmod", $"755 {MakeAbsolute(Directory(sourceFolder))}/Jackett/jackett");
		RunLinuxCommand("chmod", $"755 {MakeAbsolute(Directory(sourceFolder))}/Jackett/JackettUpdater");

		string systemdScript = MakeAbsolute(Directory(sourceFolder)) + "/Jackett/install_service_systemd.sh";
		if (FileExists(systemdScript))
		{
			RunLinuxCommand("chmod", $"755 {systemdScript}");
		}

		string macOsServiceScript = MakeAbsolute(Directory(sourceFolder)) + "/Jackett/install_service_macos";
		if (FileExists(macOsServiceScript))
		{
			RunLinuxCommand("chmod", $"755 {macOsServiceScript}");
		}

		RunLinuxCommand("tar",  $"-C {sourceFolder} -zcvf {outputDirectory}/{tarFileName}.gz {tarCdirectoryOption}");
	}	
}

private void InstallMsysTar()
{
	//Gzip is included by default with MSYS2, but not tar. Use the package manager to install tar

	var startInfo = new System.Diagnostics.ProcessStartInfo()
	{
		Arguments = "-S --noconfirm tar",
		FileName = @"C:\msys64\usr\bin\pacman.exe",
		UseShellExecute = false
	};

	var process = System.Diagnostics.Process.Start(startInfo);
	process.WaitForExit();

	if (FileExists(@"C:\msys64\usr\bin\tar.exe") && FileExists(@"C:\msys64\usr\bin\gzip.exe"))
	{
		Information("tar.exe and gzip.exe were found");
	}
	else
    {
        throw new Exception("tar.exe and gzip.exe were NOT found");   
    }
}

private void DotNetCorePublish(string projectPath, string framework, string runtime, string outputPath)
{
	bool publishSingleFile = false;

	if (publishSingleFile && framework != "net461")
	{
		var settings = new DotNetCorePublishSettings
		{
			Framework = framework,
			Runtime = runtime,
			OutputDirectory = outputPath,
			ArgumentCustomization = args=>args.Append("/p:PublishSingleFile=true")
		};

		DotNetCorePublish(projectPath, settings);
	}
	else
	{
		var settings = new DotNetCorePublishSettings
		{
			Framework = framework,
			Runtime = runtime,
			OutputDirectory = outputPath
		};

		DotNetCorePublish(projectPath, settings);
	}
}

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
	.IsDependentOn("Windows-Environment-Dev")
	.Does(() =>
	{
		Information("Default Task Completed");
	});

Task("Windows-Appveyor")
	.IsDependentOn("Windows-Environment-Appveyor")
	.Does(() =>
	{
		Information("Windows Appveyor Task Completed");
	});

Task("Linux")
	.IsDependentOn("Linux-Environment")
	.Does(() =>
	{
		Information("Linux Task Completed");
	});

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);
