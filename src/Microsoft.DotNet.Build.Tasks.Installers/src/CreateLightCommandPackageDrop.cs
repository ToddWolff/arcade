// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Microsoft.DotNet.Build.Tasks.Installers
{
    public class CreateLightCommandPackageDrop : BuildTask
    {
        private const int _fieldsArtifactId = 0;
        private const int _fieldsArtifactPath1 = 6;
        private const int _fieldsArtifactPath2 = 1;
        private const int _fieldsArtifactPath3 = 2;
        private const int _fieldsArtifactPath6 = 5;

        [Required]
        public string LightCommandWorkingDir { get; set; }
        public bool NoLogo { get; set; }
        public bool Fv { get; set; }
        public string PdbOut { get; set; }
        public string Cultures { get; set; }
        public string WixProjectFile { get; set; }
        public string ContentsFile { get; set; }
        public string OutputsFile { get; set; }
        public string BuiltOutputsFile { get; set; }
        /// <summary>
        /// Additional set of base paths that are used for resolving paths.
        /// </summary>
        public ITaskItem[] AdditionalBasePaths { get; set; }
        public ITaskItem [] Loc { get; set; }
        public ITaskItem [] Sice { get; set; }
        [Required]
        public string Out { get; set; }
        public ITaskItem [] WixExtensions { get; set; }
        [Required]
        public ITaskItem [] WixSrcFiles { get; set; }

        [Output]
        public string LightCommandPackageNameOutput { get; set; }
        // The light command that was originally used to generate the MSI.  This is purely used for informational purposes
        // and to validate that the light command being created by this task is correct (assist with debugging).
        public string OriginalLightCommand { get; set; }

        public override bool Execute()
        {
            LightCommandPackageNameOutput = Path.GetFileNameWithoutExtension(Out);
            string packageDropOutputFolder = Path.Combine(LightCommandWorkingDir, LightCommandPackageNameOutput);

            if (!Directory.Exists(packageDropOutputFolder))
            {
                Directory.CreateDirectory(packageDropOutputFolder);
            }

            XmlNamespaceManager nsmgr = new XmlNamespaceManager(new NameTable());
            nsmgr.AddNamespace("wix", "http://schemas.microsoft.com/wix/2006/objects");

            foreach (var wixSrcFile in WixSrcFiles)
            {
                // copy the file to outputPath
                string newWixSrcFilePath = Path.Combine(packageDropOutputFolder, Path.GetFileName(wixSrcFile.ItemSpec));
                File.Copy(wixSrcFile.ItemSpec, newWixSrcFilePath, true);

                string wixSrcFileExtension = Path.GetExtension(wixSrcFile.ItemSpec);
                // These files are typically .wixobj. Occasionally we have a wixlib as input, which
                // is created using light and is a binary file. When doing post-build signing,
                // it's replaced in the inputs to the light command after being reconstructed from
                // its own light command drop.
                if (wixSrcFileExtension == ".wixlib")
                {
                    continue;
                }
                else if (wixSrcFileExtension != ".wixobj")
                {
                    Log.LogError($"Wix source file extension {wixSrcFileExtension} is not supported.");
                    continue;
                }

                ProcessWixObj(newWixSrcFilePath, packageDropOutputFolder, nsmgr);
            }
            if (Loc != null)
            {
                foreach (var locItem in Loc)
                {
                    var destinationPath = Path.Combine(packageDropOutputFolder, Path.GetFileName(locItem.ItemSpec));
                    File.Copy(locItem.ItemSpec, destinationPath, true);
                }
            }

            // Write Light command to file
            string commandFilename = Path.Combine(packageDropOutputFolder, "light.cmd");
            string commandString = string.Empty;
            if(OriginalLightCommand != null)
            {
                commandString += "REM Original light command" + Environment.NewLine;
                commandString += "REM " + OriginalLightCommand + Environment.NewLine;
            }
            commandString += "REM Modified light command" + Environment.NewLine;
            commandString += "light.exe";
            commandString += $" -out {Path.GetFileName(Out)}";
            if (NoLogo)
            {
                commandString += " -nologo";
            }
            if (Cultures != null)
            {
                commandString += $" -cultures:{Cultures}";
            }
            if (Loc != null)
            {
                foreach (var locItem in Loc)
                {
                    commandString += $" -loc:{Path.GetFileName(locItem.ItemSpec)}";
                }
            }
            if(Fv)
            {
                commandString += " -fv";
            }
            if(PdbOut != null)
            {
                commandString += $" -pdbout {PdbOut}";
            }
            if(WixProjectFile != null)
            {
                var destinationPath = Path.Combine(packageDropOutputFolder, Path.GetFileName(WixProjectFile));
                File.Copy(WixProjectFile, destinationPath, true);
                commandString += $" -wixprojectfile {Path.GetFileName(WixProjectFile)}";
            }
            if(ContentsFile != null)
            {
                commandString += $" -contentsfile {Path.GetFileName(ContentsFile)}";
            }
            if (OutputsFile != null)
            {
                commandString += $" -outputsfile {Path.GetFileName(OutputsFile)}";
            }
            if (BuiltOutputsFile != null)
            {
                commandString += $" -builtoutputsfile {Path.GetFileName(BuiltOutputsFile)}";
            }
            if (Sice != null)
            {
                foreach (var siceItem in Sice)
                {
                    commandString += $" -sice:{siceItem.ItemSpec}";
                }
            }
            if(WixExtensions != null)
            {
                foreach(var wixExtension in WixExtensions)
                {
                    commandString += $" -ext {wixExtension.ItemSpec}";
                }
            }
            if(WixSrcFiles != null)
            {
                foreach(var wixSrcFile in WixSrcFiles)
                {
                    commandString += $" {Path.GetFileName(wixSrcFile.ItemSpec)}";
                }
            }
            File.WriteAllText(commandFilename, commandString);

            return !Log.HasLoggedErrors;
        }

        /// <summary>
        ///     Process a .wixobj file that is an input to the light command.
        /// </summary>
        /// <param name="wixObjFilePath">Path to the wixobj file in its new drop location</param>
        /// <param name="packageDropOutputFolder">Output light command drop folder</param>
        /// <param name="nsmgr">xml namespace manager</param>
        void ProcessWixObj(string wixObjFilePath, string packageDropOutputFolder, XmlNamespaceManager nsmgr)
        {
            Log.LogMessage(LogImportance.Normal, $"Creating modified wixobj file '{wixObjFilePath}'...");

            XDocument doc = XDocument.Load(wixObjFilePath);
            if (doc == null)
            {
                Log.LogError($"Failed to open the wixobj file '{wixObjFilePath}'");
                return;
            }

            // process fragment - WixFile elements
            // path in field 7
            string xpath = "//wix:wixObject/wix:section[@type='fragment']/wix:table[@name='WixFile']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath1);

            // process product - WixFile elements
            // path in field 7
            xpath = "//wix:wixObject/wix:section[@type='product']/wix:table[@name='WixFile']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath1);

            // process fragment - Binary elements
            // path in field 2
            xpath = "//wix:wixObject/wix:section[@type='fragment']/wix:table[@name='Binary']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath2);

            // process product - Icon elements
            // path in field 2
            xpath = "//wix:wixObject/wix:section[@type='product']/wix:table[@name='Icon']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath2);

            // process product - WixVariable elements
            // path in field 2
            xpath = "//wix:wixObject/wix:section[@type='product']/wix:table[@name='WixVariable']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath2);

            // Bundle specific items.

            // path in fields 3 and 6
            xpath = "//wix:wixObject/wix:section[@type='bundle']/wix:table[@name='Payload']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath3, _fieldsArtifactPath6);

            // process WixVariable data
            // path in field 2
            xpath = "//wix:wixObject/wix:section[@type='bundle']/wix:table[@name='WixVariable']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath2);

            // process Payload, in fragment section, data
            // path in fields 3 and 6
            xpath = "//wix:wixObject/wix:section[@type='fragment']/wix:table[@name='Payload']/wix:row";
            ProcessXPath(doc, xpath, packageDropOutputFolder, nsmgr, _fieldsArtifactPath3, _fieldsArtifactPath6);

            doc.Save(wixObjFilePath);
        }

        void ProcessXPath(XDocument doc, string xpath, string outputPath, XmlNamespaceManager nsmgr, int pathField1, int pathField2 = 0)
        {
            IEnumerable<XElement> iels = doc.XPathSelectElements(xpath, nsmgr);
            if (iels != null && iels.Count() > 0)
            {

                foreach (XElement row in iels)
                {
                    IEnumerable<XElement> fields = row.XPathSelectElements("wix:field", nsmgr);
                    if (fields == null || fields.Count() == 0)
                    {
                        Log.LogError($"No fields in row ('{xpath}') of document '{doc.BaseUri}'");
                        continue;
                    }

                    int count = 0;
                    string id = null;
                    string oldPath = null;
                    string newRelativePath = null;
                    bool foundArtifact = false;
                    bool isVariableRef = false;

                    foreach (XElement field in fields)
                    {
                        if (count == _fieldsArtifactId)
                        {
                            id = field.Value;
                        }
                        else if (count == pathField1)
                        {
                            oldPath = field.Value;

                            // Potentially make oldPath the absolute if it's not, using the additional base
                            // paths. It's possible that the path is a variable. In this case,
                            // we can ignore it.
                            if (oldPath.StartsWith("!("))
                            {
                                isVariableRef = true;
                                break;
                            }
                            else if (!Path.IsPathRooted(oldPath))
                            {
                                if (AdditionalBasePaths == null)
                                {
                                    // Break here, will log an error below.
                                    break;
                                }
                                foreach (var additionalBasePath in AdditionalBasePaths)
                                {
                                    var possiblePath = Path.Combine(additionalBasePath.ItemSpec, oldPath);
                                    if (File.Exists(possiblePath))
                                    {
                                        oldPath = possiblePath;
                                        foundArtifact = true;
                                        break;
                                    }
                                }
                            }
                            else if (File.Exists(oldPath))
                            {
                                foundArtifact = true;
                            }
                            else
                            {
                                break;
                            }

                            newRelativePath = Path.Combine(id, Path.GetFileName(oldPath));
                            field.Value = newRelativePath;
                        }
                        else if (pathField2 != 0 && count == pathField2)
                        {
                            field.Value = newRelativePath;
                            break;
                        }
                        count++;
                    }

                    if (!isVariableRef)
                    {
                        if (foundArtifact)
                        {
                            string newFolder = Path.Combine(outputPath, id);
                            if (!Directory.Exists(newFolder))
                            {
                                Directory.CreateDirectory(newFolder);
                            }

                            File.Copy(oldPath, Path.Combine(outputPath, newRelativePath), true);
                        }
                        else if (oldPath == null)
                        {
                            Log.LogError($"Could not locate a file within {row}");
                        }
                        else
                        {
                            Log.LogError($"Could not locate file {oldPath}. Please ensure the file exists and/or pass AdditionalBasePaths for non-rooted file paths.");
                        }
                    }
                }
            }
        }
    }
}
