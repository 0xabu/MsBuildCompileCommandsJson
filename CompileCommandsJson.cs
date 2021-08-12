using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Web;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// MSBuild logger to emit a compile_commands.json file from a C++ project build.
/// </summary>
/// <remarks>
/// Based on the work of:
///   * Kirill Osenkov and the MSBuildStructuredLog project.
///   * Dave Glick's MsBuildPipeLogger.
///
/// Ref for MSBuild Logger API:
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
        //
        string outputFilePath = String.IsNullOrEmpty(Parameters) ? "compile_commands.json" : Parameters;

        try
        {
            const bool append = false;
            Encoding utf8WithoutBom = new UTF8Encoding(false);
            this.streamWriter = new StreamWriter(outputFilePath, append, utf8WithoutBom);
            this.firstLine = true;
            streamWriter.WriteLine("[");
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
                throw new LoggerException("Failed to create " + outputFilePath + ": " + ex.Message);
            }
            else
            {
                // Unexpected failure
                throw;
            }
        }

        eventSource.AnyEventRaised += EventSource_AnyEventRaised;
    }

    private void EventSource_AnyEventRaised(object sender, BuildEventArgs args)
    {
        if (args is TaskCommandLineEventArgs taskArgs && taskArgs.TaskName == "CL")
        {
            string dirname = Path.GetDirectoryName(taskArgs.ProjectFile);
            string[] cmdArgs = CommandLineToArgs(taskArgs.CommandLine);

            // Options that consume the following argument.
            string[] optionsWithParam = {
                "D", "I", "F", "U", "FI", "FU", 
                "analyze:log", "analyze:stacksize", "analyze:max_paths",
                "analyze:ruleset", "analyze:plugin"};

            List<string> maybeFilenames = new List<string>();
            List<string> filenames = new List<string>();
            bool allFilenamesAreSources = false;

            for (int i = 1; i < cmdArgs.Length; i++)
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
            string compileCommand =
                Path.GetFullPath(cmdArgs[0]) + taskArgs.CommandLine.Substring(cmdArgs[0].Length);

            // For each source file, emit a JSON entry
            foreach (string filename in filenames)
            {
                // Terminate the preceding entry
                if (firstLine)
                {
                    firstLine = false;
                }
                else
                {
                    streamWriter.WriteLine(",");
                }

                // Write one entry
                streamWriter.WriteLine(String.Format(
                    "{{\"directory\": \"{0}\",",
                    HttpUtility.JavaScriptStringEncode(dirname)));
                streamWriter.WriteLine(String.Format(
                    " \"command\": \"{0}\",",
                    HttpUtility.JavaScriptStringEncode(compileCommand)));
                streamWriter.Write(String.Format(
                    " \"file\": \"{0}\"}}",
                    HttpUtility.JavaScriptStringEncode(filename)));
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
        if (!firstLine)
        {
            streamWriter.WriteLine();
        }
        streamWriter.WriteLine("]");
        streamWriter.Close();
        base.Shutdown();
    }

    private StreamWriter streamWriter;
    private bool firstLine;
}
