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
/// MSBuild logger to emit a compile_commands.json file.
/// </summary>
/// <remarks>
/// Based on the work of:
///   * Kirill Osenkov and the MSBuildStructuredLog project.
///   * Dave Glick's MsBuildPipeLogger.
/// Format spec:
/// https://clang.llvm.org/docs/JSONCompilationDatabase.html
/// </remarks>
public class CompileCommandsLogger : Logger
{
    public override void Initialize(IEventSource eventSource)
    {
        try
        {
            // Open the file
            const bool append = false;
            Encoding utf8WithoutBom = new UTF8Encoding(false);
            this.streamWriter = new StreamWriter("compile_commands.json", append, utf8WithoutBom);
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
                throw new LoggerException("Failed to create compile_commands.json: " + ex.Message);
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
            List<string> filenames = new List<string>();
            for (int i = 1; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i] == "/D")
                {
                    i++; // skip next arg
                }
                else if (cmdArgs[i].StartsWith("/"))
                {
                    // other argument, ignore it
                }
                else
                {
                    filenames.Add(cmdArgs[i]);
                }
            }

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
                    HttpUtility.JavaScriptStringEncode(taskArgs.CommandLine)));
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
