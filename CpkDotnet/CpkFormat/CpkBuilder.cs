using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CpkFormat;

public sealed class CpkBuilder
{
    public int Mode { get; set; } = 1;
    public int Alignment { get; set; } = 0x800;
    public bool Compress { get; set; }
    public string Tvers { get; set; } = "CPKMC2.47.02, DLL3.17.00";
    public int MaxThreads { get; set; } = Environment.ProcessorCount;

    public List<(string Path, byte[] Data, bool IsRaw)> Files { get; } = new();
    public byte[]? GtocData { get; set; }
    public List<long>? FileIds { get; set; }

    public static byte[]? LoadGtocFromCpk(string cpkPath)
    {
        using var archive = CpkArchive.Open(cpkPath);
        return archive.ReadGtoc();
    }

    public void LoadGtocFromFile(string filePath)
    {
        GtocData = File.ReadAllBytes(filePath);
    }

    public static void ApplyPreset(CpkBuilder builder, string presetName)
    {
        switch (presetName)
        {
            case "pad3ds":
                builder.Mode = 3;
                builder.Tvers = "CPKMC2.47.02, DLL3.17.00";
                break;
        }
    }

    public static List<long> LoadIdsFromCpk(string cpkPath)
    {
        using var archive = CpkArchive.Open(cpkPath);
        var ids = new List<long>(archive.Files.Count);
        foreach (var entry in archive.Files)
            ids.Add(entry.Id);
        return ids;
    }

    private sealed class FileBuildInfo
    {
        public string DirName { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public long ExtractSize { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    public static CpkBuilder FromArchive(CpkArchive archive)
    {
        var builder = new CpkBuilder
        {
            Mode = archive.Mode,
            Alignment = archive.Alignment,
            Tvers = archive.Tvers ?? "CPKMC2.47.02, DLL3.17.00",
            FileIds = new List<long>(),
            GtocData = archive.ReadGtoc()
        };

        foreach (var entry in archive.Files)
        {
            byte[] raw = archive.ReadFileRaw(entry);
            builder.Files.Add((entry.FullPath, raw, true));
            builder.FileIds.Add(entry.Id);
        }

        return builder;
    }

    public void AddFile(string archivePath, byte[] data, bool isRaw = false)
    {
        Files.Add((archivePath, data, isRaw));
    }

    public void AddDirectory(string dirPath)
    {
        dirPath = Path.GetFullPath(dirPath);

        foreach (string filePath in Directory.EnumerateFiles(
            dirPath, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(dirPath, filePath)
                .Replace('\\', '/');
            byte[] data = File.ReadAllBytes(filePath);
            Files.Add((relPath, data, false));
        }
    }

    public void Build(string outputPath, Action<string, int, int>? progress = null)
    {
        if (Files.Count == 0)
            throw new InvalidOperationException("No files to pack");

        int align = Alignment;
        int numFiles = Files.Count;

        if (Mode == 3 && GtocData == null)
        {
            GtocData = GenerateGtoc(numFiles);
        }

        var pending = Files.OrderBy(f => CriSortKey(f.Path)).ToList();

        var fileInfos = new FileBuildInfo[pending.Count];
        int completedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, MaxThreads)
        };

        Parallel.For(0, pending.Count, parallelOptions, i =>
        {
            var (arcPath, data, isRaw) = pending[i];

            int lastSlash = arcPath.LastIndexOf('/');
            string dirName = lastSlash >= 0
                ? arcPath[..lastSlash]
                : "";
            string fileName = lastSlash >= 0
                ? arcPath[(lastSlash + 1)..]
                : arcPath;

            long extractSize;
            byte[] fileData;
            long fileSize;

            if (isRaw)
            {
                fileData = data;
                fileSize = data.Length;
                if (data.Length >= 16 && Crilayla.IsCompressed(data))
                {
                    extractSize = BinaryPrimitives.ReadInt32LittleEndian(
                        data.AsSpan(8, 4)) + 256;
                }
                else
                {
                    extractSize = data.Length;
                }
            }
            else if (Compress && data.Length >= 256)
            {
                byte[] compressed = Crilayla.Compress(data);
                if (compressed.Length < data.Length)
                {
                    fileData = compressed;
                    fileSize = compressed.Length;
                    extractSize = data.Length;
                }
                else
                {
                    fileData = data;
                    fileSize = data.Length;
                    extractSize = data.Length;
                }
            }
            else
            {
                fileData = data;
                fileSize = data.Length;
                extractSize = data.Length;
            }

            fileInfos[i] = new FileBuildInfo
            {
                DirName = dirName,
                FileName = fileName,
                FileSize = fileSize,
                ExtractSize = extractSize,
                Data = fileData
            };

            int done = Interlocked.Increment(ref completedCount);
            progress?.Invoke($"Compressing {fileName}", done, numFiles);
        });

        long enabledPacked = fileInfos.Sum(fi => fi.FileSize + fi.ExtractSize);

        var tocTable = new UtfTable { Name = "CpkTocInfo" };
        tocTable.Columns = new List<UtfColumn>
        {
            new("DirName", CriTypeId.String, CriStorageFlag.Data, ""),
            new("FileName", CriTypeId.String, CriStorageFlag.Data, ""),
            new("FileSize", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("ExtractSize", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("FileOffset", CriTypeId.ULLong, CriStorageFlag.Data, 0),
            new("ID", CriTypeId.UInt, CriStorageFlag.Data, 0),
            new("UserString", CriTypeId.String, CriStorageFlag.Constant, "<NULL>"),
        };

        for (int i = 0; i < fileInfos.Length; i++)
        {
            var fi = fileInfos[i];
            tocTable.Rows.Add(new Dictionary<string, object?>
            {
                ["DirName"] = fi.DirName,
                ["FileName"] = fi.FileName,
                ["FileSize"] = (uint)fi.FileSize,
                ["ExtractSize"] = (uint)fi.ExtractSize,
                ["FileOffset"] = 0UL,
                ["ID"] = FileIds != null && i < FileIds.Count ? (uint)FileIds[i] : (uint)(i + 1),
            });
        }

        byte[] tocUtf = tocTable.Build();
        long tocChunkSize = 16 + tocUtf.Length;
        if (tocChunkSize % align != 0)
            tocChunkSize += align - (tocChunkSize % align);

        long gtocOffset = GtocData != null ? 0x800 + tocChunkSize : 0;
        long contentOffset;
        if (GtocData != null)
        {
            contentOffset = 0x800 + tocChunkSize + GtocData.Length;
            if (contentOffset % align != 0)
                contentOffset += align - (contentOffset % align);
        }
        else
        {
            contentOffset = 0x800 + tocChunkSize;
        }

        long fileDataOffset = contentOffset - 0x800;
        for (int i = 0; i < fileInfos.Length; i++)
        {
            tocTable.Rows[i]["FileOffset"] = (ulong)fileDataOffset;
            long padded = fileInfos[i].FileSize;
            if (padded % align != 0)
                padded += align - (padded % align);
            fileDataOffset += padded;
        }

        tocUtf = tocTable.Build();
        tocChunkSize = 16 + tocUtf.Length;
        if (tocChunkSize % align != 0)
            tocChunkSize += align - (tocChunkSize % align);

        gtocOffset = GtocData != null ? 0x800 + tocChunkSize : 0;
        if (GtocData != null)
        {
            contentOffset = 0x800 + tocChunkSize + GtocData.Length;
            if (contentOffset % align != 0)
                contentOffset += align - (contentOffset % align);
        }
        else
        {
            contentOffset = 0x800 + tocChunkSize;
        }

        fileDataOffset = contentOffset - 0x800;
        for (int i = 0; i < fileInfos.Length; i++)
        {
            tocTable.Rows[i]["FileOffset"] = (ulong)fileDataOffset;
            long padded = fileInfos[i].FileSize;
            if (padded % align != 0)
                padded += align - (padded % align);
            fileDataOffset += padded;
        }

        tocUtf = tocTable.Build();
        tocChunkSize = 16 + tocUtf.Length;
        if (tocChunkSize % align != 0)
            tocChunkSize += align - (tocChunkSize % align);

        gtocOffset = GtocData != null ? 0x800 + tocChunkSize : 0;
        if (GtocData != null)
        {
            contentOffset = 0x800 + tocChunkSize + GtocData.Length;
            if (contentOffset % align != 0)
                contentOffset += align - (contentOffset % align);
        }
        else
        {
            contentOffset = 0x800 + tocChunkSize;
        }

        long contentSize = 0;
        foreach (var fi in fileInfos)
        {
            long padded = fi.FileSize;
            if (padded % align != 0)
                padded += align - (padded % align);
            contentSize += padded;
        }

        long gtocSize = GtocData?.Length ?? 0;
        long etocOffset = contentOffset + contentSize;

        var etocTable = new UtfTable { Name = "CpkEtocInfo" };
        etocTable.Columns = new List<UtfColumn>
        {
            new("UpdateDateTime", CriTypeId.ULLong, CriStorageFlag.Data, 0UL),
            new("LocalDir",       CriTypeId.String, CriStorageFlag.Data, ""),
        };

        for (int i = 0; i < fileInfos.Length; i++)
        {
            etocTable.Rows.Add(new Dictionary<string, object?>
            {
                ["UpdateDateTime"] = 0x07E5000000000000UL,
                ["LocalDir"] = fileInfos[i].DirName,
            });
        }
        etocTable.Rows.Add(new Dictionary<string, object?>
        {
            ["UpdateDateTime"] = 0UL,
            ["LocalDir"] = "",
        });

        byte[] etocUtf = etocTable.Build();
        int etocChunkSize = 16 + etocUtf.Length;

        var cpkTable = new UtfTable { Name = "CpkHeader" };
        cpkTable.Columns = new List<UtfColumn>
        {
            new("UpdateDateTime",    CriTypeId.ULLong, CriStorageFlag.Data,     1UL),
            new("FileSize",          CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ContentOffset",     CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("ContentSize",       CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocOffset",         CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocSize",           CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TocCrc",            CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("EtocOffset",        CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("EtocSize",          CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("ItocOffset",        CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ItocSize",          CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("ItocCrc",           CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("GtocOffset",        CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("GtocSize",          CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("GtocCrc",           CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("EnabledPackedSize", CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("EnabledDataSize",   CriTypeId.ULLong, CriStorageFlag.Data,     0UL),
            new("TotalDataSize",     CriTypeId.ULLong, CriStorageFlag.Constant,  0UL),
            new("Tocs",              CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Files",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("Groups",            CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("Attrs",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("TotalFiles",        CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Directories",       CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Updates",           CriTypeId.UInt,   CriStorageFlag.Constant,  0U),
            new("Version",           CriTypeId.UShort, CriStorageFlag.Data,     (ushort)7),
            new("Revision",          CriTypeId.UShort, CriStorageFlag.Data,     (ushort)1),
            new("Align",             CriTypeId.UShort, CriStorageFlag.Data,     (ushort)align),
            new("Sorted",            CriTypeId.UShort, CriStorageFlag.Data,     (ushort)1),
            new("EID",               CriTypeId.UShort, CriStorageFlag.Constant,  (ushort)0),
            new("CpkMode",           CriTypeId.UInt,   CriStorageFlag.Data,     (uint)Mode),
            new("Tvers",             CriTypeId.String, CriStorageFlag.Data,      Tvers),
            new("Comment",           CriTypeId.String, CriStorageFlag.Constant,  ""),
            new("Codec",             CriTypeId.UInt,   CriStorageFlag.Data,     0U),
            new("DpkItoc",           CriTypeId.UInt,   CriStorageFlag.Data,     0U),
        };

        cpkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["UpdateDateTime"] = 1UL,
            ["ContentOffset"] = (ulong)contentOffset,
            ["ContentSize"] = (ulong)contentSize,
            ["TocOffset"] = 0x800UL,
            ["TocSize"] = (ulong)(tocUtf.Length + 16),
            ["EtocOffset"] = (ulong)etocOffset,
            ["EtocSize"] = (ulong)etocChunkSize,
            ["GtocOffset"] = (ulong)gtocOffset,
            ["GtocSize"] = (ulong)gtocSize,
            ["GtocCrc"] = 0U,
            ["EnabledPackedSize"] = (ulong)enabledPacked,
            ["EnabledDataSize"] = (ulong)enabledPacked,
            ["Files"] = (uint)fileInfos.Length,
            ["Groups"] = GtocData != null ? 2U : 0U,
            ["Attrs"] = GtocData != null ? 1U : 0U,
            ["Version"] = (ushort)7,
            ["Revision"] = (ushort)1,
            ["Align"] = (ushort)align,
            ["Sorted"] = (ushort)1,
            ["CpkMode"] = (uint)Mode,
            ["Tvers"] = Tvers,
            ["Codec"] = 0U,
            ["DpkItoc"] = 0U,
        });

        byte[] cpkUtf = cpkTable.Build();

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 4096, FileOptions.SequentialScan);

        byte[] cpkChunk = new byte[CriConstants.CHUNK_SIZE];
        CriConstants.CPK_MAGIC.CopyTo(cpkChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(8, 4), cpkUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(cpkChunk.AsSpan(12, 4), 0);
        cpkUtf.CopyTo(cpkChunk.AsSpan(16, cpkUtf.Length));
        CriConstants.CRI_FOOTER.CopyTo(
            cpkChunk.AsSpan(CriConstants.CHUNK_SIZE - CriConstants.CRI_FOOTER.Length,
                CriConstants.CRI_FOOTER.Length));
        fs.Write(cpkChunk);

        byte[] tocChunk = new byte[tocChunkSize];
        CriConstants.TOC_MAGIC.CopyTo(tocChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(8, 4), tocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(tocChunk.AsSpan(12, 4), 0);
        tocUtf.CopyTo(tocChunk.AsSpan(16, tocUtf.Length));
        fs.Write(tocChunk);

        if (GtocData != null)
        {
            fs.Write(GtocData);
            long gtocEnd = 0x800 + tocChunkSize + GtocData.Length;
            if (gtocEnd % align != 0)
            {
                int pad = align - (int)(gtocEnd % align);
                fs.Write(new byte[pad]);
            }
        }

        for (int i = 0; i < fileInfos.Length; i++)
        {
            var fi = fileInfos[i];
            progress?.Invoke($"Writing {fi.FileName}", i, numFiles);

            fs.Write(fi.Data);

            int padLen = 0;
            if (fi.Data.Length % align != 0)
                padLen = align - (fi.Data.Length % align);
            if (padLen > 0)
                fs.Write(new byte[padLen]);
        }

        byte[] etocChunk = new byte[etocChunkSize];
        CriConstants.ETOC_MAGIC.CopyTo(etocChunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(8, 4), etocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(etocChunk.AsSpan(12, 4), 0);
        etocUtf.CopyTo(etocChunk.AsSpan(16, etocUtf.Length));
        fs.Write(etocChunk);

        progress?.Invoke("Done", numFiles, numFiles);
    }

    public byte[] GenerateGtoc(int numFiles)
    {
        int totalFlinkRows = numFiles + 8;

        var glinkTable = new UtfTable { Name = "CpkGtocGlink" };
        glinkTable.Columns = new List<UtfColumn>
        {
            new("Gname", CriTypeId.String, CriStorageFlag.Data, ""),
            new("Next", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("Child", CriTypeId.Int, CriStorageFlag.Data, 0),
        };
        glinkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Gname"] = "",
            ["Next"] = 0,
            ["Child"] = -1,
        });
        glinkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Gname"] = "(none)",
            ["Next"] = 0,
            ["Child"] = 0,
        });
        byte[] gdata = glinkTable.Build();

        var flinkTable = new UtfTable { Name = "CpkGtocFlink" };
        flinkTable.Columns = new List<UtfColumn>
        {
            new("Aindex", CriTypeId.UShort, CriStorageFlag.PerRow, (ushort)0),
            new("Next", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("Child", CriTypeId.Int, CriStorageFlag.Data, 0),
            new("SortFlink", CriTypeId.Int, CriStorageFlag.Data, 0),
        };
        flinkTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Next"] = -1,
            ["Child"] = -1,
            ["SortFlink"] = totalFlinkRows - 1,
        });
        for (int i = 1; i <= numFiles; i++)
        {
            int next = (i == numFiles) ? -1 : i + 1;
            flinkTable.Rows.Add(new Dictionary<string, object?>
            {
                ["Next"] = next,
                ["Child"] = -1,
                ["SortFlink"] = i + 1,
            });
        }
        for (int i = numFiles + 1; i < totalFlinkRows; i++)
        {
            flinkTable.Rows.Add(new Dictionary<string, object?>
            {
                ["Next"] = -1,
                ["Child"] = -1,
                ["SortFlink"] = -1,
            });
        }
        byte[] fdata = flinkTable.Build();

        var attrTable = new UtfTable { Name = "CpkGtocAttr" };
        attrTable.Columns = new List<UtfColumn>
        {
            new("Aname", CriTypeId.String, CriStorageFlag.Data, ""),
            new("Align", CriTypeId.UShort, CriStorageFlag.Data, (ushort)0),
            new("Files", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("FileSize", CriTypeId.UInt, CriStorageFlag.Data, 0U),
        };
        attrTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Aname"] = "",
            ["Align"] = (ushort)0x800,
            ["Files"] = 0U,
            ["FileSize"] = 0U,
        });
        byte[] attrData = attrTable.Build();

        var gtocTable = new UtfTable { Name = "CpkGtocInfo" };
        gtocTable.Columns = new List<UtfColumn>
        {
            new("Glink", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Flink", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Attr", CriTypeId.UInt, CriStorageFlag.Data, 0U),
            new("Gdata", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
            new("Fdata", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
            new("AttrData", CriTypeId.Bytes, CriStorageFlag.Data, Array.Empty<byte>()),
        };
        gtocTable.Rows.Add(new Dictionary<string, object?>
        {
            ["Glink"] = 2U,
            ["Flink"] = (uint)totalFlinkRows,
            ["Attr"] = 1U,
            ["Gdata"] = gdata,
            ["Fdata"] = fdata,
            ["AttrData"] = attrData,
        });
        byte[] gtocUtf = gtocTable.Build();

        int chunkSize = 16 + gtocUtf.Length;
        byte[] gtocChunk = new byte[chunkSize];
        gtocChunk[0] = (byte)'G';
        gtocChunk[1] = (byte)'T';
        gtocChunk[2] = (byte)'O';
        gtocChunk[3] = (byte)'C';
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(4, 4), 0xFF);
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(8, 4), gtocUtf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(gtocChunk.AsSpan(12, 4), 0);
        Array.Copy(gtocUtf, 0, gtocChunk, 16, gtocUtf.Length);

        return gtocChunk;
    }

    private static string CriSortKey(string path)
    {
        return string.Create(path.Length, path, (span, s) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                span[i] = c == '_' ? '~' : c;
            }
        });
    }
}