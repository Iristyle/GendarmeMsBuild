﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Collections.Generic;
using System.Xml;

namespace GendarmeMsBuild
{
    public class Gendarme : Task
    {
        private string _gendarmeExeFilename = Path.Combine(Path.Combine(ProgramFilesx86(), "Gendarme"), "gendarme.exe");
        private static readonly Regex LineRegex = new Regex(@"(?<file>.*)\(≈?((?<line>\d+)|unavailable)(,(?<column>\d+))?\)", RegexOptions.Compiled);

        #region Task Properties
        /// <summary>
        /// The path to Gendarme.exe. Defaults to C:\program Files\gendarme\gendarme.exe (or C:\program files (x86)\gendarme\gendarme.exe on 64bit systems) if no value is supplied.
        /// </summary>
        public string GendarmeExeFilename
        {
            get { return _gendarmeExeFilename; }
            set { _gendarmeExeFilename = value; }
        }

        /// <summary>
        /// The assemblies to inspect. Multiple files and masks ('?', '*') are supported. Required.
        /// </summary>
        [Required]
        public ITaskItem[] Assemblies { get; set; }

        /// <summary>
        /// The path to the Gendarme config file. Maps to --config [filename] (optional)
        /// </summary>
        public string GendarmeConfigFilename { get; set; }
        /// <summary>
        /// The name of the ruleset to be used. Maps to --ruleset [set] (optional)
        /// </summary>
        public string Ruleset { get; set; }
        /// <summary>
        /// The path to the Gendarme ignore file. Maps to --ignore [filename] (optional)
        /// </summary>
        public string GendarmeIgnoreFilename { get; set; }
        /// <summary>
        /// The inspection severity. Maps to --severity [all | audit[+] | low[+|-] | medium[+|-] | high[+|-] | critical[-]] (optional)
        /// </summary>
        public string Severity { get; set; }
        /// <summary>
        /// The confidence level defects are filtered by. Maps to --confidence [all | low[+] | normal[+|-] | high[+|-] | total[-]] (optional)
        /// </summary>
        public string Confidence { get; set; }

        private int? limit = null;
        /// <summary>
        /// Limit the amount of defects found. Maps to --limit [value] (optional)
        /// </summary>
        public int Limit
        {
            get { return limit.HasValue ? limit.Value : -1; }
            set { limit = value >= 0 ? new int?(value) : null; }
        }
        /// <summary>
        /// The path to save Gendarme's output XML (optional)
        /// </summary>
        public string OutputXmlFilename { get; set; }
        /// <summary>
        /// Output minimal info. Maps to --quiet. Also causes the MSBuild task to output no info (optional). Ignored when Visual Studio integration is enabled.
        /// </summary>
        public bool Quiet { get; set; }
        /// <summary>
        /// Output verbose info. Maps to --verbose (optional). Ignored when Visual Studio integration is enabled.
        /// </summary>
        public bool Verbose { get; set; }
        /// <summary>
        /// Whether or not to fail the build if defects are found. Defaults to false. Useful when only the 
        /// output XML is required. Ignored when Visual Studio integration is enabled.
        /// </summary>
        [Obsolete("use WarningsAsErrors instead")]
        public bool DefectsCauseFailure
        {
            get { return WarningsAsErrors; }
            set { WarningsAsErrors = value; }
        }

        /// <summary>
        /// Whether to consider defects (which are normally treated as warnings) as errors. When set to true, 
        /// the build will fail if any defects are found. Optional.
        /// </summary>
        public bool WarningsAsErrors { get; set; }

        /// <summary>
        /// Whether or not to format the output in a format Visual Studio can understand. Defaults to false (optional)
        /// </summary>
        public bool IntegrateWithVisualStudio { get; set; }

        /// <summary>
        /// Whether to display gendarme defects in msbuild output.
        /// </summary>
        public bool Silent { get; set; }
        #endregion

        /// <summary>
        /// Execute the MSBuild task
        /// </summary>
        /// <returns>True if no defects are found, false otherwise.</returns>
        public override bool Execute()
        {
            if (!VerifyProperties()) return false;

            var thisOutputFile = OutputXmlFilename;
            bool isUsingTempFile = false, keepTempFile = false;
            if (string.IsNullOrEmpty(thisOutputFile))
            {
                thisOutputFile = Path.GetTempFileName();
                isUsingTempFile = true;
            }
            else
            {
                if (File.Exists(thisOutputFile))
                    try
                    {
                        File.Delete(thisOutputFile);
                    }
                    catch (Exception e)
                    {
                        Log.LogErrorFromException(new Exception("Couldn't recreate output file " + thisOutputFile, e));
                        return false;
                    }
            }
            MaybeLogMessage("output file: " + thisOutputFile);

            try
            {
                var commandLineArguments = BuildCommandLineArguments(thisOutputFile);

                MaybeLogMessage("GendarmeMsBuild - command line arguments to Gendarme: " + commandLineArguments);
                var processInfo = new ProcessStartInfo(_gendarmeExeFilename, commandLineArguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                var sw = new Stopwatch();
                sw.Start();
                var proc = Process.Start(processInfo);
                var stdErr = proc.StandardError.ReadToEnd();
                var stdOut = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var exitCode = proc.ExitCode;
                sw.Stop();
                MaybeLogMessage(String.Format("GendarmeMsBuild - finished running Gendarme in {0}ms", sw.ElapsedMilliseconds));
                if (exitCode != 0)
                {
                    if (stdErr.Length > 0)
                    {
                        // problem with the call out to Gendarme
                        Log.LogError(stdErr);
                        return false;
                    }
                    else
                    {
                        if (!IntegrateWithVisualStudio && !string.IsNullOrEmpty(stdOut))
                            Log.LogMessage(stdOut);

                        if (!Silent)
                        {
                            try { CreateVisualStudioOutput(thisOutputFile); }
                            catch (Exception ex)
                            {
                                keepTempFile = true;
                                Log.LogError("[gendarmeMsBuild]", "ErrorProcessingGendarmeInput", null, thisOutputFile, 1, 1, 0, 0,
                                    String.Format("A call to Gendarme with command line arguments {0} failed{1}Delete temporary file when done examining.  Error message:{1}{2}", commandLineArguments, Environment.NewLine, ex));
                            }
                        }
                        return !WarningsAsErrors;
                    }
                }
                if (!IntegrateWithVisualStudio && !string.IsNullOrEmpty(stdOut))
                    Log.LogMessage(stdOut);
                return true;
            }
            finally
            {
                if (isUsingTempFile && !keepTempFile)
                    try { File.Delete(thisOutputFile); }
                    catch { /* do nothing */}
            }
        }

        #region helper methods/classes
        private string BuildCommandLineArguments(string thisOutputFile)
        {
            var sb = new StringBuilder();
            if (GendarmeConfigFilename != null)
                sb.Append(" --config ").Append('"').Append(GendarmeConfigFilename).Append('"');
            if (Ruleset != null)
                sb.Append(" --set ").Append('"').Append(Ruleset).Append('"');
            if (Severity != null)
                sb.Append(" --severity ").Append('"').Append(Severity).Append('"');
            if (Confidence != null)
                sb.Append(" --confidence ").Append('"').Append(Confidence).Append('"');
            if (GendarmeIgnoreFilename != null)
                sb.Append(" --ignore \"").Append(GendarmeIgnoreFilename).Append('"');
            if (limit.HasValue)
                sb.Append(" --limit ").Append(limit.Value.ToString());
            if (Quiet)
                sb.Append(" --quiet");
            if (Verbose)
                sb.Append(" --verbose");
            sb.Append(" --xml \"").Append(thisOutputFile).Append('"');
            foreach (var assembly in Assemblies)
                sb.Append(" \"").Append(assembly.ItemSpec).Append('"');
            return sb.ToString();
        }

        private bool VerifyProperties()
        {
            if (!File.Exists(GendarmeExeFilename))
            {
                Log.LogError("Couldn't find gendarme.exe at " + GendarmeExeFilename);
                return false;
            }
            if (!string.IsNullOrEmpty(GendarmeIgnoreFilename) && !File.Exists(GendarmeIgnoreFilename))
            {
                Log.LogError("Couldn't find the Gendarme ignore file at " + GendarmeExeFilename);
                return false;
            }
            return true;
        }


        private void CreateVisualStudioOutput(string outputFile)
        {
            var xdoc = XDocument.Load(outputFile);

            var q = from defect in xdoc.Root.Descendants("defect")
                    let rule = defect.Parent.Parent
                    let target = defect.Parent
                    group defect by new
                    {
                        RuleName = rule.Attribute("Name").Value,
                        //Uri = rule.Attribute("Uri").Value, //.Substring(rule.Attribute("Uri").Value.LastIndexOf('/') + 1).Replace('#', '.'),
                        Problem = rule.Element("problem").Value,
                        Solution = rule.Element("solution").Value,
                        Target = target.Attribute("Name").Value,
                        Source = LineRegex.IsMatch(defect.Attribute("Source").Value) ? defect.Attribute("Source").Value : null,
                    } into grouping
                    select new
                       {
                           Description = string.Join(Environment.NewLine, grouping.Select(d => d.Value).OrderBy(s => s).Distinct().ToArray()),
                           RuleName = grouping.Key.RuleName,
                           //Uri = rule.Attribute("Uri").Value, //.Substring(rule.Attribute("Uri").Value.LastIndexOf('/') + 1).Replace('#', '.'),
                           Problem = grouping.Key.Problem,
                           Solution = grouping.Key.Solution,
                           Source = grouping.Key.Source,
                           Target = grouping.Key.Target
                       };
            foreach (var defect in q)
            {
                if (defect.Source != null)
                {
                    var groups = LineRegex.Match(defect.Source).Groups;
                    int line = SafeConvert(groups["line"].Value), column = SafeConvert(groups["column"].Value);
                    LogDefect("[gendarme]", defect.RuleName, null, groups["file"].Value, line, column, line, column, String.Format("{0}: {1}{2}{3}", defect.RuleName, defect.Problem, Environment.NewLine, defect.Description));
                }
                else
                {
                    LogDefect("[gendarme]", defect.RuleName, null, null, 0, 0, 0, 0, String.Format("{0}: {1}: {2}{3}{4}", defect.RuleName, defect.Target, defect.Problem, Environment.NewLine, defect.Description));
                }
            }
        }

        private static int SafeConvert(string number)
        {
            try { return Convert.ToInt32(number); }
            catch { return 0; }
        }

        /// <summary>
        /// Log a message to MSBuild if Visual Studio integration isn't enabled, and the Quiet option isn't set.
        /// </summary>
        /// <param name="message"></param>
        private void MaybeLogMessage(string message)
        {
            if (!IntegrateWithVisualStudio && !Quiet)
                Log.LogMessage(message);
        }

        private void LogDefect(string subcategory, string errorCode, string helpKeyword, string file, int lineNumber,
                             int columnNumber, int endLineNumber, int endColumnNumber, string message, params string[] messageArgs)
        {
            if (WarningsAsErrors)
                Log.LogError(subcategory, errorCode, helpKeyword, file, lineNumber, columnNumber, endLineNumber, endColumnNumber, message, messageArgs);
            else
                Log.LogWarning(subcategory, errorCode, helpKeyword, file, lineNumber, columnNumber, endLineNumber, endColumnNumber, message, messageArgs);
        }

        static string ProgramFilesx86()
        {
            if (8 == IntPtr.Size
                || (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"))))
            {
                return Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            }

            return Environment.GetEnvironmentVariable("ProgramFiles");
        }
        #endregion
    }
}
