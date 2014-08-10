﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
//using Amazon;
//using Amazon.S3;
//using Amazon.S3.Util;
//using Amazon.S3.Model;
using System.Net;
using System.Net.Security;
using System.Diagnostics;
using System.Xml.Linq;
//using Amazon.S3.Transfer;
using SharpKit.Release.Utils;
using System.Xml;
using SharpKit.Utils;
using SharpKit.Installer.Builder;
using SharpKit.Installer;
using Octokit;

namespace SharpKit.Release
{
    class Program
    {

        string ProductVersion { get; set; }
        string SetupFilename { get; set; }
        public string InstallerProjectDir { get; set; }

        string GitRoot;

        static void Main(string[] args)
        {
            new Program().Run();
        }

        void Run()
        {
            GitRoot = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "..", "..", "..")).FullName + Path.DirectorySeparatorChar;
            InstallerProjectDir = Path.Combine(GitRoot, "Installer", "Installer") + Path.DirectorySeparatorChar;
            ReleaseLogsDir = GitRoot + @"Installer\Packager\ReleaseLogs\";
            ReadVersion();

            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2)
            {
                ProcessCommand(args[1]);
                return;
            }

            Console.WriteLine("Note: you can automate all commands via packer.exe <command>");

            while (true)
            {
                ReadVersion();

                Console.WriteLine("CurrentVersion " + ProductVersion);
                Console.WriteLine();
                Console.WriteLine("Please choose a command:");
                Console.WriteLine();
                Console.WriteLine("create-version: Creates a new version. Note: Compiler and SDK will be recompiled, because js headers will change");
                Console.WriteLine("commit: Commits the modified files with commit message containing the version. You can do this via external tool.");
                Console.WriteLine("push: Pushes the changes to github. You can do this via external tool.");
                Console.WriteLine("create-release: Creates a github release, with the changelog as description. Note: Before running this command, a push is requied!");
                Console.WriteLine("create-installer");
                Console.WriteLine("upload");
                Console.WriteLine("rollback: Reverts all changed version files to its original state. Assues, that they are not commited. Note: It's not fully implemented.");
                Console.WriteLine("exit");

                Console.WriteLine();
                Console.Write("Command: ");
                var cmd = Console.ReadLine();
                if (cmd == "exit") return;
                ProcessCommand(cmd);
                Console.WriteLine();
                Console.WriteLine();
            }

        }

        public void ReadVersion()
        {
            ProductVersion = File.ReadAllLines(Path.Combine(GitRoot, "VERSION"))[0];
        }

        public void ProcessCommand(string cmd)
        {
            switch (cmd)
            {

                case "create-version":
                    {
                        Console.WriteLine("Enter new version (optional): ");
                        var v = Console.ReadLine();
                        if (v.IsNotNullOrEmpty())
                        {
                            ProductVersion = v;
                            CreateNewVersion();
                            return;
                        }
                        break;
                    }

                case "create-installer":
                    {
                        CreateInstaller();
                        break;
                    }

                case "create-release":
                    {
                        CreateGitHubRelease();
                        break;
                    }

                case "upload":
                    {
                        Upload();
                        break;
                    }

                case "rollback":
                    {
                        Rollback();
                        break;
                    }

                default:
                    Console.WriteLine("Unknown command / not implemented");
                    break;
            }
        }

        ReleaseLog ReleaseLog;
        //ReleaseLog LastReleaseLog;

        void UpdateSharpKitVersionInfoSourceFiles(ReleaseLog log)
        {
            UpdateSharpKitVersionInfoSourceFiles(log.Version, log.Created);
        }
        void UpdateSharpKitVersionInfoSourceFiles(string version, DateTime dt)
        {
            var line = VersionInfoToCode(version, dt);
            line = "            " + line + ",";
            var files = GitRoot.ToDirectoryInfo().GetFiles("SharpKitVersionInfo.cs", SearchOption.AllDirectories).Select(t => t.FullName).ToList();
            files.ForEach(t => InsertLinesAfterPlaceHolder(t, new[] { line }));
        }

        private void InsertLinesAfterPlaceHolder(string filename, string[] lines2)
        {
            var lines = File.ReadAllLines(filename).ToList();
            var index = lines.FindIndex(t => t.Contains("<!--Placeholder-->"));
            lines.InsertRange(index + 1, lines2);
            File.WriteAllLines(filename, lines.ToArray());
        }

        private string VersionInfoToCode(string version, DateTime dt)
        {
            var line = String.Format("new SharpKitVersionInfo {{ Version = \"{0}\", Date = new DateTime({1}, {2}, {3}) }}", version, dt.Year, dt.Month, dt.Day);
            return line;
        }

        private void FillLog()
        {
            //var lastReleaseLog = LastReleaseLog;
            ReleaseLog.SharpKit5 = CreateSolutionInfo(GitRoot);
            ReleaseLog.SharpKit_Sdk = CreateSolutionInfo(Path.Combine(GitRoot, "SDK"));

            ShowLogMessages(ReleaseLog.SharpKit5.SvnLogEntries);
            ShowLogMessages(ReleaseLog.SharpKit_Sdk.SvnLogEntries);
        }

        void ShowLogMessages(List<VersionControlLogEntry> log)
        {
            var msgs = GetLogMessages(log);
            msgs.ForEach(t => Console.WriteLine(t));
        }

        private List<string> GetLogMessages(List<VersionControlLogEntry> log)
        {
            var msgs = log.Select(t => t.msg).Where(t => t.IsNotNullOrEmpty()).ToList();
            return msgs;
        }

        SolutionInfo CreateSolutionInfo(string srcDir)
        {
            var si = new SolutionInfo { HeadRevision = GetHeadRevision(srcDir) };
            si.SvnLogEntries = GetGitLog(srcDir, GetLastVersion()); //hack by dan-el
            return si;
        }

        List<VersionControlLogEntry> GetGitLog(string dir, string fromRevision)
        {
            //Sample output
            /*
             * commit d34362948c6d2a40527c2b77dc270da61e9f40fb
             * Author: Sebastian Loncar <sebastian.loncar@gmail.com>
             * Date:   2013-11-23 17:30:12 +0100
             *     (empty line)
             * commit test
             *     (empty line)
             * line1
             * line2
             * "line3"
             *     (empty line)
             */

            //git --no-pager log fromRevision..HEAD --pretty=format:"COMMIT=%h;AUTHOR=%an;DATE=%ad;COMMENT=%s" --date=iso //Has no extended descriptions!
            //git --no-pager log fromRevision..HEAD --date=iso //contains also the extended descriptions
            //git --no-pager log --date=iso
            var cmd = String.Format("--no-pager log {0}..HEAD --date=iso", fromRevision);
            var res = Execute(dir, GitExecutable, cmd);
            var lines = res.Output.Select(t => t == null ? "" : t).ToArray();

            var list = new List<VersionControlLogEntry>();

            VersionControlLogEntry entry = null;
            var comments = new List<string>();

            foreach (var line in lines)
            {
                if (line.StartsWith("commit "))
                {
                    if (entry != null)
                    {
                        entry.msg = string.Join(Environment.NewLine, comments.Where(t => t.IsNotNullOrEmpty()));
                        list.Add(entry);
                    }
                    entry = new VersionControlLogEntry();
                    comments.Clear();

                    entry.revision = line.Substring("commit ".Length);
                }
                else if (line.StartsWith("Author: "))
                {
                    entry.author = line.Substring("Author: ".Length);
                }
                else if (line.StartsWith("Date: "))
                {
                    entry.date = DateTime.Parse(line.Substring("Date: ".Length));
                }
                else if (line.StartsWith("    "))
                {
                    comments.Add(line.Trim());
                }
            }

            if (entry != null)
            {
                entry.msg = string.Join(Environment.NewLine, comments.Where(t => t.IsNotNullOrEmpty()));
                list.Add(entry);
            }

            //var list = doc.Root.Elements().Select(el => new SvnLogEntry
            //{
            //    author = el.GetChildValue<string>("author"),
            //    date = el.GetChildValue<DateTime>("date"),
            //    msg = el.GetChildValue<string>("msg"),
            //    revision = el.GetChildValue<string>("revision"),
            //}).ToList();

            list.Where(t => t.msg != null && t.msg.EndsWith("\n")).ForEach(t => t.msg = t.msg.RemoveLast(1));
            return list;
        }

        string GetHeadRevision(string dir)
        {
            return GetGitHeadRevision(dir);
        }

        string GitExecutable = @"C:\Program Files (x86)\Git\bin\git.exe";
        string GetGitHeadRevision(string dir)
        {
            //git --no-pager log HEAD -1 --pretty=format:"%h"
            var res = Execute(dir, GitExecutable, "--no-pager log HEAD -1 --pretty=format:\"%H\"");
            return res.Output[0];
        }

        public static ExecuteResult Execute(string dir, string file, string args)
        {
            Console.WriteLine("Executing: {0} {1} {2}", dir, file, args);
            var process = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = dir,
                FileName = file,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            var res = new ExecuteResult { Output = new List<string>(), Error = new List<string>() };

            Console.WriteLine("{0}>{1} {2}", process.StartInfo.WorkingDirectory, process.StartInfo.FileName, process.StartInfo.Arguments);
            process.OutputDataReceived += (s, e) => { Console.WriteLine(e.Data); res.Output.Add(e.Data); };
            process.ErrorDataReceived += (s, e) => { Console.WriteLine(e.Data); res.Error.Add(e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            process.WaitForExit();
            res.ExitCode = process.ExitCode;
            if (process.ExitCode != 0)
                throw new Exception(String.Format("Error during execution, exit code={0}", process.ExitCode));
            Console.WriteLine("Finished execution. Exit code: {0}", process.ExitCode);
            return res;
        }

        public string ReleaseLogsDir;

        public void CreateNewVersion()
        {
            ReleaseLog = new ReleaseLog { Created = DateTime.Now, Filename = Path.Combine(ReleaseLogsDir, ProductVersion + ".xml"), Version = ProductVersion };
            FillLog();
            ReleaseLog.Save();

            UpdateVersionFiles();

            SharpKit.Installer.Builder.Utils.CallMake(Path.Combine(GitRoot, "Compiler"));
            SharpKit.Installer.Builder.Utils.CallMake(Path.Combine(GitRoot, "SDK")); //Because the the js files contains the version in the header, the files need to be regenerated before commit

            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("The new version is now changed in all files. Please commit and push it to git now!");
            Console.ForegroundColor = old;

            //CommitGit();
        }

        public void CommitGit()
        {
            CallGit("add .");
            CallGit("commit -am 'New Release " + ProductVersion + "'");
            CallGit("push origin master");
        }

        public void CallGit(string args)
        {
            //
        }

        public void UpdateVersionFiles()
        {
            File.WriteAllText(Path.Combine(GitRoot, "VERSION"), ProductVersion);
            //UpdateSharpKitVersionInfoSourceFiles(ReleaseLog);
            UpdateAssemblyFileVersions(ProductVersion);
        }

        //TODO
        public void Rollback()
        {

            ProductVersion = GetLastVersion();
            File.WriteAllText(Path.Combine(GitRoot, "VERSION"), ProductVersion);
            UpdateAssemblyFileVersions(ProductVersion);
        }

        string[] SharpKitCompilerProjectNames = new string[]
        {
            "CSharp.Tasks",
            "skc5",
        };

        void UpdateAssemblyFileVersions(string version)
        {
            //foreach (var dir in Directory.GetDirectories(SdkSrcDir))
            //{
            //    var file = Path.Combine(dir, "Properties\\AssemblyInfo");
            //    if (File.Exists(file))
            //    {
            //        UpdateAssemblyFileVersion(file, version);
            //    }
            //}

            foreach (var project in SharpKitCompilerProjectNames)
            {
                var file = GitRoot + "\\Compiler\\" + project + "\\Properties\\AssemblyInfo.cs";
                if (File.Exists(file))
                    UpdateAssemblyFileVersion(file, version);
            }
            //UpdateAssemblyFileVersion(@"C:\Projects\SharpJs\lib\NRefactory\ICSharpCode.NRefactory\Properties\GlobalAssemblyInfo.cs", version);
            //UpdateAssemblyFileVersion(@"C:\Projects\SharpJs\lib\Mono.Cecil\Mono.Cecil\AssemblyInfo.cs", version);

        }

        private void UpdateAssemblyFileVersion(string file, string version)
        {
            Console.WriteLine("Updating {0}", file);
            var lines = File.ReadAllLines(file).ToList();
            var index = lines.FindIndex(t => t.StartsWith("[assembly: AssemblyFileVersion("));
            if (index < 0)
            {
                lines.Add("");
                index = lines.Count - 1;
            }
            Console.WriteLine(lines[index]);
            lines[index] = String.Format("[assembly: AssemblyFileVersion(\"{0}\")]", version);
            Console.WriteLine(lines[index]);
            File.WriteAllLines(file, lines);
        }

        public void CreateGitHubRelease()
        {
            var github = new GitHubClient(new ProductHeaderValue("TestGitHutAPI"));
            github.Credentials = new Credentials(Config.GitHubAccessToken);
            github.Release.Create(Config.GitHubUser, Config.GitHubRepoCompiler, new ReleaseUpdate(ProductVersion)).Wait();
        }

        public void Upload()
        {
            try
            {
                var github = new GitHubClient(new ProductHeaderValue("TestGitHutAPI"));
                github.Credentials = new Credentials(Config.GitHubAccessToken);
                var rels = github.Release.GetAll(Config.GitHubUser, Config.GitHubRepoCompiler);
                rels.Wait();
                foreach (var rel in rels.Result)
                {
                    if (rel.TagName == ProductVersion)
                    {
                        Octokit.Internal.Request.DefaultTimeout = TimeSpan.FromSeconds(1000);
                        Stream str = new MemoryStream();
                        var _bytes = File.ReadAllBytes(SetupFilename);
                        str.Write(_bytes, 0, _bytes.Length);
                        str.Seek(0, SeekOrigin.Begin);

                        github.Release.UploadAsset(rel, new ReleaseAssetUpload() { ContentType = "application/exe", FileName = Path.GetFileName(SetupFilename), RawData = str }).Wait();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

        }

        public string GetLastVersion()
        {
            var github = new GitHubClient(new ProductHeaderValue("TestGitHutAPI"));
            github.Credentials = new Credentials(Config.GitHubAccessToken);
            var rels = github.Release.GetAll(Config.GitHubUser, Config.GitHubRepoCompiler);
            rels.Wait();
            return rels.Result.Last().TagName;
        }

        void CreateInstaller()
        {
            var maker = new SetupMaker
            {
                ProductVersion = ProductVersion,
                GitRoot = GitRoot,
                InstallerProjectDir = InstallerProjectDir,
            };
            maker.Run();
            SetupFilename = maker.SetupFilename;
        }

        //public static void BuildProject(string slnFilename, string configuration, string projectName, string action = "build")
        //{
        //    Console.WriteLine("Building: {0} {1} {2}", slnFilename, configuration, projectName);
        //    var args = String.Format("\"{0}\" /{1} \"{2}\"", Path.GetFileName(slnFilename), action, configuration);
        //    if (projectName.IsNotNullOrEmpty())
        //        args += String.Format(" /Project \"{0}\"", projectName);
        //    //args += " /consoleloggerparameters:ErrorsOnly";
        //    var outFile = @"C:\temp\BuildOutput.txt";
        //    if (File.Exists(outFile)) File.Delete(outFile);
        //    args += @" /Out " + outFile;
        //    var res = Execute(Path.GetDirectoryName(slnFilename), Vs2013Exe, args);
        //    if (res.ExitCode != 0)
        //        throw new Exception(String.Format("Error during build, exit code={0}", res.ExitCode));
        //    Console.WriteLine("Finished build.");
        //}

    }
}