using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

/// <summary>
/// MSBuild logger to emit a compile_commands.json file from a C++ project build.
/// Arguments (all arguments are optional and order does not matter): 
/// path:[a valid path, relative or absolute] - Where to output the file, if no option is specified it will output in the working directory
/// task:[task name] - A custom task name to search for. We check if the task name from MSBuild contains this string.
///     This is useful for distributed build systems that sometimes use their own custom CL task.
///
/// Argument examples
/// None - /logger:path/to/CompileCommands.dll
/// Path - /logger:path/to/CompileCommands.dll;path:custom/path/here.json
/// Task - /logger:path/to/CompileCommands.dll;task:customTaskName
/// Both - /logger:path/to/CompileCommands.dll;path:custom/path/here.json,task:customTaskName
///
/// </summary>
/// <remarks>
/// Based on the work of:
///   * Kirill Osenkov and the MSBuildStructuredLog project.
///   * Dave Glick's MsBuildPipeLogger.
///   * Iterative build support and custom task names added by Andrew Richardson
///
///
/// Ref for MSBuild Logge\r API:
///   https://docs.microsoft.com/en-us/visualstudio/msbuild/build-loggers
/// Format spec:
///   https://clang.llvm.org/docs/JSONCompilationDatabase.html
/// </remarks>
public class CompileCommandsJson : Logger
{
    public override void Initialize(IEventSource eventSource)
    {
        // Default to writing compile_commands.json in the current directory,
        // but permit it to be overridden by a parameter.
        outputFilePath = "compile_commands.json";

        if(!string.IsNullOrEmpty(Parameters))
        {
            string[] args = Parameters.Split(',');

            for(int i = 0; i < args.Length; ++i)
            {
                string arg = args[i];
                if(arg.ToLower().StartsWith("path:"))
                {
                    outputFilePath = arg.Substring(5);
                }
                else if(arg.ToLower().StartsWith("task:"))
                {
                    customTask = arg.Substring(5);
                }
                else
                {
                    throw new LoggerException($"Unknown argument in compile command logger: {arg}");
                }
            }
        }

        eventSource.AnyEventRaised += EventSource_AnyEventRaised;
        try
        {
            commandLookup = new Dictionary<string, CompileCommand>();
            if (File.Exists(outputFilePath))
            {
                compileCommands = JsonConvert.DeserializeObject<List<CompileCommand>>(File.ReadAllText(outputFilePath));
            }

			//AR - Not an else because it is possible for JsonConvert.DeserializeObject to return null
            if(compileCommands == null)
            {
                compileCommands = new List<CompileCommand>();
            }

            //AR - Create a dictionary for cleaner and faster cache lookup
            //We could refactor the code to read and write directly to the cache
            //but there is no discernable performance difference even on very large code bases
            foreach(CompileCommand command in compileCommands)
            {
                commandLookup.Add(command.file, command);
            }
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException
                || ex is ArgumentNullException
                || ex is PathTooLongException
                || ex is DirectoryNotFoundException
                || ex is NotSupportedException
                || ex is ArgumentException
                || ex is SecurityException
                || ex is IOException)
            {
                throw new LoggerException($"Failed to create {outputFilePath}: {ex.Message}");
            }
            else
            {
                // Unexpected failure
                throw;
            }
        }

    }

    private void EventSource_AnyEventRaised(object sender, BuildEventArgs args)
    {
        if (args is TaskCommandLineEventArgs taskArgs 
            && (taskArgs.TaskName == "CL" || (!string.IsNullOrEmpty(customTask) && taskArgs.TaskName.Contains(customTask))))
        {
            
            // taskArgs.CommandLine begins with the full path to the compiler, but that path is
            // *not* escaped/quoted for a shell, and may contain spaces, such as C:\Program Files
            // (x86)\Microsoft Visual Studio\... As a workaround for this misfeature, find the
            // end of the path by searching for CL.exe. (This will fail if a user renames the
            // compiler binary, or installs their tools to a path that includes "CL.exe ".)
            const string clExe = "cl.exe ";
            int clExeIndex = taskArgs.CommandLine.ToLower().IndexOf(clExe);
            if (clExeIndex == -1)
            {
                throw new LoggerException($"Unexpected lack of CL.exe in {taskArgs.CommandLine}");
            }

            string compilerPath = taskArgs.CommandLine.Substring(0, clExeIndex + clExe.Length - 1);
            string argsString = taskArgs.CommandLine.Substring(clExeIndex + clExe.Length).TrimStart();
            string[] cmdArgs = CommandLineToArgs(argsString);

            // Options that consume the following argument.
            string[] optionsWithParam = {
                "D", "I", "F", "U", "FI", "FU", 
                "analyze:log", "analyze:stacksize", "analyze:max_paths",
                "analyze:ruleset", "analyze:plugin"};

            List<string> maybeFilenames = new List<string>();
            List<string> filenames = new List<string>();
            bool allFilenamesAreSources = false;

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                bool isOption = cmdArgs[i].StartsWith("/") || cmdArgs[i].StartsWith("-");
                string option = isOption ? cmdArgs[i].Substring(1) : "";

                if (isOption && Array.Exists(optionsWithParam, e => e == option))
                {
                    i++; // skip next arg
                }
                else if (option == "Tc" || option == "Tp")
                {
                    // next arg is definitely a source file
                    if (i + 1 < cmdArgs.Length)
                    {

                        filenames.Add(cmdArgs[i + 1]);
                    }
                }
                else if (option.StartsWith("Tc") || option.StartsWith("Tp"))
                {
                    // rest of this arg is definitely a source file
                    filenames.Add(option.Substring(2));
                }
                else if (option == "TC" || option == "TP")
                {
                    // all inputs are treated as source files
                    allFilenamesAreSources = true;
                }
                else if (option == "link")
                {
                    break; // only linker options follow
                }
                else if (isOption || cmdArgs[i].StartsWith("@"))
                {
                    // other argument, ignore it
                }
                else
                {
                    // non-argument, add it to our list of potential sources
                    maybeFilenames.Add(cmdArgs[i]);
                }
            }

            // Iterate over potential sources, and decide (based on the filename)
            // whether they are source inputs.
            foreach (string filename in maybeFilenames)
            {
                if (allFilenamesAreSources)
                {
                    filenames.Add(filename);
                }
                else
                {
                    int suffixPos = filename.LastIndexOf('.');
                    if (suffixPos != -1)
                    {
                        string ext = filename.Substring(suffixPos + 1).ToLowerInvariant();
                        if (ext == "c" || ext == "cxx" || ext == "cpp")
                        {
                            filenames.Add(filename);
                        }
                    }
                }
            }

            // simplify the compile command to avoid .. etc.
            string compileCommand = '"' + Path.GetFullPath(compilerPath) + "\" " + argsString;
            string dirname = Path.GetDirectoryName(taskArgs.ProjectFile);

            // For each source file, a CompileCommand entry
            foreach (string filename in filenames)
            {
                // AR - Iterative build support, we loaded in the existing compile_commands file in the init.
                // Now we check to see if an entry for the filename exists, if if does we overwrite
                // the previous result, if it doesn't we add a new entry. We then write the entire list
                // when the logger shuts down.
                CompileCommand command;
                if (commandLookup.ContainsKey(filename))
                {
                    command = commandLookup[filename];
                    command.file = filename;
                    command.directory = dirname;
                    command.command = compileCommand;
                }
                else
                {
                    command = new CompileCommand() { file = filename, directory = dirname, command = compileCommand };
                    compileCommands.Add(command);
                    commandLookup.Add(filename, command);
                }

            }
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    static string[] CommandLineToArgs(string commandLine)
    {
        int argc;
        var argv = CommandLineToArgvW(commandLine, out argc);        
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var args = new string[argc];
            for (var i = 0; i < args.Length; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p);
            }

            return args;
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
        }
    }

    public override void Shutdown()
    {
        File.WriteAllText(outputFilePath, JsonConvert.SerializeObject(compileCommands, Formatting.Indented));
        base.Shutdown();
    }

    class CompileCommand
    {
        public string directory;
        public string command;
        public string file;
    }

    string customTask;
    string outputFilePath;
    private List<CompileCommand> compileCommands;
    private Dictionary<string, CompileCommand> commandLookup;
}