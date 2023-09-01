﻿using AssetStudio;
using AssetStudioCLI.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static AssetStudioCLI.Exporter;
using static CubismLive2DExtractor.Live2DExtractor;
using Ansi = AssetStudioCLI.CLIAnsiColors;

namespace AssetStudioCLI
{
    internal static class Studio
    {
        public static AssetsManager assetsManager = new AssetsManager();
        public static List<AssetItem> parsedAssetsList = new List<AssetItem>();
        public static AssemblyLoader assemblyLoader = new AssemblyLoader();
        private static Dictionary<AssetStudio.Object, string> containers =
            new Dictionary<AssetStudio.Object, string>();

        static Studio()
        {
            Progress.Default = new Progress<int>(ShowCurProgressValue);
        }

        private static void ShowCurProgressValue(int value)
        {
            Console.Write($"[{value:000}%]\r");
        }

        public static bool LoadAssets()
        {
            var isLoaded = false;
            assetsManager.SpecifyUnityVersion = CLIOptions.o_unityVersion.Value;
            assetsManager.SetAssetFilter(CLIOptions.o_exportAssetTypes.Value);

            assetsManager.LoadFilesAndFolders(CLIOptions.inputPath);
            if (assetsManager.assetsFileList.Count == 0)
            {
                Logger.Warning("No Unity file can be loaded.");
            }
            else
            {
                isLoaded = true;
            }

            return isLoaded;
        }

        public static void ParseAssets()
        {
            Logger.Info("Parse assets...");

            var fileAssetsList = new List<AssetItem>();
            var objectCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);

            Progress.Reset();
            var i = 0;
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    var assetItem = new AssetItem(asset);
                    assetItem.UniqueID = "_#" + i;
                    var isExportable = false;
                    switch (asset)
                    {
                        case AssetBundle m_AssetBundle:
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (int k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = m_AssetBundle.m_PreloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;

                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                if (m_Container.Value.TryGet(out var obj))
                                {
                                    containers[obj] = m_Container.Key;
                                }
                            }
                            break;
                        case Texture2D m_Texture2D:
                            if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                                assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                            assetItem.Text = m_Texture2D.m_Name;
                            break;
                        case AudioClip m_AudioClip:
                            if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                                assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                            assetItem.Text = m_AudioClip.m_Name;
                            break;
                        case VideoClip m_VideoClip:
                            if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                                assetItem.FullSize =
                                    asset.byteSize + m_VideoClip.m_ExternalResources.m_Size;
                            assetItem.Text = m_VideoClip.m_Name;
                            break;
                        case Mesh _:
                        case MovieTexture _:
                        case TextAsset _:
                        case Font _:
                        case Sprite _:
                            assetItem.Text = ((NamedObject)asset).m_Name;
                            break;
                        case Shader m_Shader:
                            assetItem.Text = m_Shader.m_ParsedForm?.m_Name ?? m_Shader.m_Name;
                            break;
                        case Material m_Material:
                            assetItem.Text = m_Material.m_Name;
                            break;
                        case MonoBehaviour m_MonoBehaviour:
                            if (
                                m_MonoBehaviour.m_Name == ""
                                && m_MonoBehaviour.m_Script.TryGet(out var m_Script)
                            )
                            {
                                assetItem.Text = m_Script.m_ClassName;
                            }
                            else
                            {
                                assetItem.Text = m_MonoBehaviour.m_Name;
                            }
                            break;
                    }
                    if (assetItem.Text == "")
                    {
                        assetItem.Text = assetItem.TypeString + assetItem.UniqueID;
                    }

                    isExportable = CLIOptions.o_exportAssetTypes.Value.Contains(asset.type);
                    if (isExportable)
                    {
                        fileAssetsList.Add(assetItem);
                    }

                    Progress.Report(++i, objectCount);
                }
                foreach (var asset in fileAssetsList)
                {
                    if (containers.ContainsKey(asset.Asset))
                    {
                        asset.Container = containers[asset.Asset];
                    }
                }
                parsedAssetsList.AddRange(fileAssetsList);
                fileAssetsList.Clear();
                if (CLIOptions.o_workMode.Value != WorkMode.ExportLive2D)
                {
                    containers.Clear();
                }
            }
            var log =
                $"Finished loading {assetsManager.assetsFileList.Count} files with {parsedAssetsList.Count} exportable assets";
            var unityVer = assetsManager.assetsFileList[0].version;
            long m_ObjectsCount;
            if (unityVer[0] > 2020)
            {
                m_ObjectsCount = assetsManager.assetsFileList.Sum(
                    x =>
                        x.m_Objects.LongCount(
                            y =>
                                y.classID != (int)ClassIDType.Shader
                                && CLIOptions.o_exportAssetTypes.Value.Any(k => (int)k == y.classID)
                        )
                );
            }
            else
            {
                m_ObjectsCount = assetsManager.assetsFileList.Sum(
                    x =>
                        x.m_Objects.LongCount(
                            y => CLIOptions.o_exportAssetTypes.Value.Any(k => (int)k == y.classID)
                        )
                );
            }
            var objectsCount = assetsManager.assetsFileList.Sum(
                x =>
                    x.Objects.LongCount(
                        y => CLIOptions.o_exportAssetTypes.Value.Any(k => k == y.type)
                    )
            );
            if (m_ObjectsCount != objectsCount)
            {
                log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
            }
            Logger.Info(log);
        }

        public static void ShowExportableAssetsInfo()
        {
            var exportableAssetsCountDict = new Dictionary<ClassIDType, int>();
            string info = "";
            if (parsedAssetsList.Count > 0)
            {
                foreach (var asset in parsedAssetsList)
                {
                    if (exportableAssetsCountDict.ContainsKey(asset.Type))
                    {
                        exportableAssetsCountDict[asset.Type] += 1;
                    }
                    else
                    {
                        exportableAssetsCountDict.Add(asset.Type, 1);
                    }
                }

                info += "\n[Exportable Assets Count]\n";
                foreach (var assetType in exportableAssetsCountDict.Keys)
                {
                    info += $"# {assetType}: {exportableAssetsCountDict[assetType]}\n";
                }
                if (exportableAssetsCountDict.Count > 1)
                {
                    info += $"#\n# Total: {parsedAssetsList.Count} assets";
                }
            }
            else
            {
                info += "No exportable assets found.";
            }

            if (CLIOptions.o_logLevel.Value > LoggerEvent.Info)
            {
                Console.WriteLine(info);
            }
            else
            {
                Logger.Info(info);
            }
        }

        public static void FilterAssets()
        {
            var assetsCount = parsedAssetsList.Count;
            var filteredAssets = new List<AssetItem>();

            switch (CLIOptions.filterBy)
            {
                case FilterBy.Name:
                    filteredAssets = parsedAssetsList.FindAll(
                        x =>
                            CLIOptions.o_filterByName.Value.Any(
                                y =>
                                    x.Text.ToString().IndexOf(y, StringComparison.OrdinalIgnoreCase)
                                    >= 0
                            )
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) "
                            + $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByName.Value)}\"".Color(Ansi.BrightYellow)} in their Names."
                    );
                    break;
                case FilterBy.Container:
                    filteredAssets = parsedAssetsList.FindAll(
                        x =>
                            CLIOptions.o_filterByContainer.Value.Any(
                                y =>
                                    x.Container
                                        .ToString()
                                        .IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0
                            )
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) "
                            + $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByContainer.Value)}\"".Color(Ansi.BrightYellow)} in their Containers."
                    );
                    break;
                case FilterBy.PathID:
                    filteredAssets = parsedAssetsList.FindAll(
                        x =>
                            CLIOptions.o_filterByPathID.Value.Any(
                                y =>
                                    x.m_PathID
                                        .ToString()
                                        .IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0
                            )
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) "
                            + $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByPathID.Value)}\"".Color(Ansi.BrightYellow)} in their PathIDs."
                    );
                    break;
                case FilterBy.NameOrContainer:
                    filteredAssets = parsedAssetsList.FindAll(
                        x =>
                            CLIOptions.o_filterByText.Value.Any(
                                y =>
                                    x.Text.ToString().IndexOf(y, StringComparison.OrdinalIgnoreCase)
                                    >= 0
                            )
                            || CLIOptions.o_filterByText.Value.Any(
                                y =>
                                    x.Container
                                        .ToString()
                                        .IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0
                            )
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) "
                            + $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByText.Value)}\"".Color(Ansi.BrightYellow)} in their Names or Contaniers."
                    );
                    break;
                case FilterBy.NameAndContainer:
                    filteredAssets = parsedAssetsList.FindAll(
                        x =>
                            CLIOptions.o_filterByName.Value.Any(
                                y =>
                                    x.Text.ToString().IndexOf(y, StringComparison.OrdinalIgnoreCase)
                                    >= 0
                            )
                            && CLIOptions.o_filterByContainer.Value.Any(
                                y =>
                                    x.Container
                                        .ToString()
                                        .IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0
                            )
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) "
                            + $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByContainer.Value)}\"".Color(Ansi.BrightYellow)} in their Containers "
                            + $"and {$"\"{string.Join("\", \"", CLIOptions.o_filterByName.Value)}\"".Color(Ansi.BrightYellow)} in their Names."
                    );
                    break;
            }
            parsedAssetsList.Clear();
            parsedAssetsList = filteredAssets;
        }

        public static void ExportAssets()
        {
            var savePath = CLIOptions.o_outputFolder.Value;
            var toExportCount = parsedAssetsList.Count;
            var exportedCount = 0;

            var groupOption = CLIOptions.o_groupAssetsBy.Value;
            foreach (var asset in parsedAssetsList)
            {
                string exportPath;
                switch (groupOption)
                {
                    case AssetGroupOption.TypeName:
                        exportPath = Path.Combine(savePath, asset.TypeString);
                        break;
                    case AssetGroupOption.ContainerPath:
                    case AssetGroupOption.ContainerPathFull:
                        if (!string.IsNullOrEmpty(asset.Container))
                        {
                            exportPath = Path.Combine(
                                savePath,
                                Path.GetDirectoryName(asset.Container)
                            );
                            if (groupOption == AssetGroupOption.ContainerPathFull)
                            {
                                exportPath = Path.Combine(
                                    exportPath,
                                    Path.GetFileNameWithoutExtension(asset.Container)
                                );
                            }
                        }
                        else
                        {
                            exportPath = savePath;
                        }
                        break;
                    case AssetGroupOption.SourceFileName:
                        if (string.IsNullOrEmpty(asset.SourceFile.originalPath))
                        {
                            exportPath = Path.Combine(
                                savePath,
                                asset.SourceFile.fileName + "_export"
                            );
                        }
                        else
                        {
                            exportPath = Path.Combine(
                                savePath,
                                Path.GetFileName(asset.SourceFile.originalPath) + "_export",
                                asset.SourceFile.fileName
                            );
                        }
                        break;
                    default:
                        exportPath = savePath;
                        break;
                }

                exportPath += Path.DirectorySeparatorChar;
                try
                {
                    switch (CLIOptions.o_workMode.Value)
                    {
                        case WorkMode.ExportRaw:
                            Logger.Debug(
                                $"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}"
                            );
                            if (ExportRawFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case WorkMode.Dump:
                            Logger.Debug(
                                $"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}"
                            );
                            if (ExportDumpFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case WorkMode.Export:
                            Logger.Debug(
                                $"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}"
                            );
                            if (ExportConvertFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"{asset.SourceFile.originalPath}: [{$"{asset.Type}: {asset.Text}".Color(Ansi.BrightRed)}] : Export error\n{ex}"
                    );
                }
                Console.Write($"Exported [{exportedCount}/{toExportCount}]\r");
            }
            Console.WriteLine("");

            if (exportedCount == 0)
            {
                Logger.Default.Log(LoggerEvent.Info, "Nothing exported.", ignoreLevel: true);
            }
            else if (toExportCount > exportedCount)
            {
                Logger.Default.Log(
                    LoggerEvent.Info,
                    $"Finished exporting {exportedCount} asset(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightYellow)}\".",
                    ignoreLevel: true
                );
            }
            else
            {
                Logger.Default.Log(
                    LoggerEvent.Info,
                    $"Finished exporting {exportedCount} asset(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightGreen)}\".",
                    ignoreLevel: true
                );
            }

            if (toExportCount > exportedCount)
            {
                Logger.Default.Log(
                    LoggerEvent.Info,
                    $"{toExportCount - exportedCount} asset(s) skipped (not extractable or file(s) already exist).",
                    ignoreLevel: true
                );
            }
        }

        public static void ExportAssetList()
        {
            var savePath = CLIOptions.o_outputFolder.Value;

            switch (CLIOptions.o_exportAssetList.Value)
            {
                case ExportListType.XML:
                    var filename = Path.Combine(savePath, "assets.xml");
                    var doc = new XDocument(
                        new XElement(
                            "Assets",
                            new XAttribute("filename", filename),
                            new XAttribute("createdAt", DateTime.UtcNow.ToString("s")),
                            parsedAssetsList.Select(
                                asset =>
                                    new XElement(
                                        "Asset",
                                        new XElement("Name", asset.Text),
                                        new XElement("Container", asset.Container),
                                        new XElement(
                                            "Type",
                                            new XAttribute("id", (int)asset.Type),
                                            asset.TypeString
                                        ),
                                        new XElement("PathID", asset.m_PathID),
                                        new XElement("Source", asset.SourceFile.fullName),
                                        new XElement("Size", asset.FullSize)
                                    )
                            )
                        )
                    );
                    doc.Save(filename);

                    break;
                case ExportListType.JSON: // Assuming you've added this to the ExportListType enum
                    var jsonFilename = Path.Combine(savePath, "assets.json");

                    var assetsObject = new
                    {
                        filename = jsonFilename,
                        createdAt = DateTime.UtcNow.ToString("s"),
                        Assets = parsedAssetsList
                            .Select(
                                asset =>
                                    new
                                    {
                                        Name = asset.Text,
                                        Container = asset.Container,
                                        Type = new
                                        {
                                            id = (int)asset.Type,
                                            typeString = asset.TypeString
                                        },
                                        PathID = asset.m_PathID,
                                        Source = asset.SourceFile.fullName,
                                        Size = asset.FullSize
                                    }
                            )
                            .ToList()
                    };

                    var jsonString = JsonConvert.SerializeObject(assetsObject, Formatting.Indented);
                    File.WriteAllText(jsonFilename, jsonString);

                    break;
            }
            Logger.Info($"Finished exporting asset list with {parsedAssetsList.Count} items.");
        }

        public static void ExportLive2D()
        {
            var baseDestPath = Path.Combine(CLIOptions.o_outputFolder.Value, "Live2DOutput");
            var useFullContainerPath = false;

            Progress.Reset();
            Logger.Info($"Searching for Live2D files...");

            var cubismMocs = parsedAssetsList
                .Where(x =>
                {
                    if (x.Type == ClassIDType.MonoBehaviour)
                    {
                        ((MonoBehaviour)x.Asset).m_Script.TryGet(out var m_Script);
                        return m_Script?.m_ClassName == "CubismMoc";
                    }
                    return false;
                })
                .Select(x => x.Asset)
                .ToArray();
            if (cubismMocs.Length == 0)
            {
                Logger.Default.Log(
                    LoggerEvent.Info,
                    "Live2D Cubism models were not found.",
                    ignoreLevel: true
                );
                return;
            }
            if (cubismMocs.Length > 1)
            {
                var basePathSet = cubismMocs
                    .Select(x => containers[x].Substring(0, containers[x].LastIndexOf("/")))
                    .ToHashSet();

                if (basePathSet.Count != cubismMocs.Length)
                {
                    useFullContainerPath = true;
                    Logger.Debug($"useFullContainerPath: {useFullContainerPath}");
                }
            }
            var basePathList = useFullContainerPath
                ? cubismMocs.Select(x => containers[x]).ToList()
                : cubismMocs
                    .Select(x => containers[x].Substring(0, containers[x].LastIndexOf("/")))
                    .ToList();
            var lookup = containers.ToLookup(
                x =>
                    basePathList.Find(
                        b =>
                            x.Value.Contains(b)
                            && x.Value.Split('/').Any(y => y == b.Substring(b.LastIndexOf("/") + 1))
                    ),
                x => x.Key
            );

            var totalModelCount = lookup.LongCount(x => x.Key != null);
            Logger.Info($"Found {totalModelCount} model(s).");
            var name = "";
            var modelCounter = 0;
            foreach (var assets in lookup)
            {
                var container = assets.Key;
                if (container == null)
                    continue;
                name = container;

                Logger.Info(
                    $"[{modelCounter + 1}/{totalModelCount}] Exporting Live2D: \"{container.Color(Ansi.BrightCyan)}\""
                );
                try
                {
                    var modelName = useFullContainerPath
                        ? Path.GetFileNameWithoutExtension(container)
                        : container.Substring(container.LastIndexOf('/') + 1);
                    container = Path.HasExtension(container)
                        ? container.Replace(Path.GetExtension(container), "")
                        : container;
                    var destPath =
                        Path.Combine(baseDestPath, container) + Path.DirectorySeparatorChar;

                    ExtractLive2D(assets, destPath, modelName, assemblyLoader);
                    modelCounter++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Live2D model export error: \"{name}\"", ex);
                }
                Progress.Report(modelCounter, (int)totalModelCount);
            }
            var status =
                modelCounter > 0
                    ? $"Finished exporting [{modelCounter}/{totalModelCount}] Live2D model(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightCyan)}\""
                    : "Nothing exported.";
            Logger.Default.Log(LoggerEvent.Info, status, ignoreLevel: true);
        }
    }
}
