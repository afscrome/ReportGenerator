using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by mprof (Mono).
    /// </summary>
    internal class MProfParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(MProfParser));

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static readonly Regex LambdaMethodNameRegex = new Regex("<.*>.+__", RegexOptions.Compiled);

        /// <summary>
        /// Initializes a new instance of the <see cref="MProfParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal MProfParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var methods = report.Descendants("method").ToArray();

            var assemblyNames = report.Descendants("assembly")
                .Select(a => a.Attribute("name").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(methods, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), false, this.ToString());
            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="methods">The methods.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement[] methods, string assemblyName)
        {
            Logger.DebugFormat(Resources.CurrentAssembly, assemblyName);

            var classNames = methods
                .Where(m => m.Attribute("assembly").Value.Equals(assemblyName))
                .Select(m => m.Attribute("class").Value)
                .Where(c => !c.Contains(".`1c__") && c != "`1")
                .Distinct()
                .Where(c => this.ClassFilter.IsElementIncludedInReport(c))
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => this.ProcessClass(methods, assembly, className));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="methods">The methods.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        private void ProcessClass(XElement[] methods, Assembly assembly, string className)
        {
            var filesOfClass = methods
                .Where(m => m.Attribute("assembly").Value.Equals(assembly.Name))
                .Where(m => m.Attribute("class").Value.Equals(className, StringComparison.Ordinal))
                .Where(m => m.Attribute("filename").Value.Length > 0)
                .Select(m => m.Attribute("filename").Value)
                .Distinct()
                .ToArray();

            var filteredFilesOfClass = filesOfClass
                .Where(f => this.FileFilter.IsElementIncludedInReport(f))
                .ToArray();

            // If all files are removed by filters, then the whole class is omitted
            if ((filesOfClass.Length == 0 && !this.FileFilter.HasCustomFilters) || filteredFilesOfClass.Length > 0)
            {
                var @class = new Class(className, assembly);

                foreach (var file in filteredFilesOfClass)
                {
                    @class.AddFile(ProcessFile(methods, @class, file));
                }

                assembly.AddClass(@class);
            }
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="methods">The methods.</param>
        /// <param name="class">The class.</param>
        /// <param name="filePath">The file path.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(XElement[] methods, Class @class, string filePath)
        {
            var methodsOfFile = methods
                .Where(m => m.Attribute("assembly").Value.Equals(@class.Assembly.Name))
                .Where(m => m.Attribute("class").Value.Equals(@class.Name, StringComparison.Ordinal))
                .Where(m => m.Attribute("filename").Value.Equals(filePath))
                .Distinct()
                .ToArray();

            var linesOfFile = methodsOfFile
                .Elements("statement")
                .Select(l => new
                {
                    LineNumber = int.Parse(l.Attribute("line").Value, CultureInfo.InvariantCulture),
                    Visits = l.Attribute("counter").Value.ParseLargeInteger()
                })
                .OrderBy(l => l.LineNumber)
                .ToArray();

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (linesOfFile.Length > 0)
            {
                coverage = new int[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];
                lineVisitStatus = new LineVisitStatus[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var line in linesOfFile)
                {
                    int visits = line.Visits > 0 ? 1 : 0;
                    coverage[line.LineNumber] = coverage[line.LineNumber] == -1 ? visits : Math.Min(coverage[line.LineNumber] + visits, 1);
                    lineVisitStatus[line.LineNumber] = lineVisitStatus[line.LineNumber] == LineVisitStatus.Covered || line.Visits > 0 ? LineVisitStatus.Covered : LineVisitStatus.NotCovered;
                }
            }

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus);

            SetCodeElements(codeFile, methodsOfFile);

            return codeFile;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                string methodName = method.Attribute("name").Value;

                if (LambdaMethodNameRegex.IsMatch(methodName))
                {
                    continue;
                }

                CodeElementType type = CodeElementType.Method;

                if (methodName.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                    || methodName.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var lineNumbers = method
                    .Elements("statement")
                    .Select(l => int.Parse(l.Attribute("line").Value, CultureInfo.InvariantCulture))
                    .ToArray();

                if (lineNumbers.Length > 0)
                {
                    int firstLine = lineNumbers.Min();
                    int lastLine = lineNumbers.Max();

                    codeFile.AddCodeElement(new CodeElement(
                        methodName,
                        type,
                        firstLine,
                        lastLine,
                        codeFile.CoverageQuota(firstLine, lastLine)));
                }
            }
        }
    }
}
