using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections.Generic;
using Mono.Collections.Generic;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

using System.Text;
using System.Text.RegularExpressions;

using System.Diagnostics;

using Mono.Options;
using System.Security.Policy;
using System.IO.MemoryMappedFiles;
using System.IO.Compression;
using System.ComponentModel;
using System.Globalization;

namespace DumpSymbolicate
{

    internal class MonoStateThread
    {
        internal MonoStateFrame[] ManagedFrames;
        internal MonoStateFrame[] NativeFrames;

        internal string Name;

        public MonoStateThread (MonoStateFrame[] managed_frames,  MonoStateFrame[] unmanaged_frames, string name)
        {
            if (managed_frames == null)
                throw new Exception("Only non-null frames allowed for reporting");
            
            this.ManagedFrames = managed_frames;
            this.NativeFrames = unmanaged_frames;
            this.Name = name;
        }

        public void Emit (JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Name");
            writer.WriteValue(this.Name);

            writer.WritePropertyName("ManagedFrames");
            writer.WriteStartArray();
            foreach (var frame in ManagedFrames)
                frame.Emit(writer);
            writer.WriteEnd();

            writer.WritePropertyName("NativeFrames");
            writer.WriteStartArray();
            foreach (var frame in NativeFrames)
                frame.Emit(writer);
            writer.WriteEnd();

            writer.WriteEndObject();
        }

        public MonoStateThread()
        {
        }
    }

    internal abstract class MonoStateFrame
    {
        public abstract void Emit (JsonWriter writer);
    }

    internal class MonoStateUnmanagedFrame : MonoStateFrame
    {
        internal string address;
        internal string name;

        public override void Emit(JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Address");
            writer.WriteValue(address);
            writer.WritePropertyName("Name");
            writer.WriteValue(name);
            writer.WriteEndObject();
        }
    }

    internal class MonoStateManagedFrame : MonoStateFrame
    {
        public string mvid;
        public uint token;
        public uint offset;

        public string assembly;
        internal string klass;
        internal string function;
        internal string file;

        internal int start_line;
        internal int start_col;
        internal int end_line;
        internal int end_col;

        public override void Emit(JsonWriter writer)
        {
            writer.WriteStartObject();

            if (assembly != null) {
                writer.WritePropertyName("Assembly");
                writer.WriteValue(this.assembly);
                writer.WritePropertyName("Class");
                writer.WriteValue(this.klass);
                writer.WritePropertyName("Function");
                writer.WriteValue(this.function);

                writer.WritePropertyName("File");
                writer.WriteValue(this.file);
                writer.WritePropertyName("Line");
                writer.WriteValue(this.start_line);
            } else {
                writer.WritePropertyName("GUID");
                writer.WriteValue(this.mvid);
                writer.WritePropertyName("Token");
                writer.WriteValue(this.token);
                writer.WritePropertyName("Offset");
                writer.WriteValue(this.offset);
            }

            writer.WriteEndObject();
        }

    }

    class NativeMapper
    {
        class NativeMethod
        {
            public string Name { get; set; }
            public string FileName { get; set; }
        }

        Dictionary<string, NativeMethod> offsetToMethodName;

        public string MonoExePath { get; set; }

        Process llvm_process;

        public NativeMapper (string indexFile)
        {
            if (string.IsNullOrEmpty (indexFile))
            {
                return;
            }
            offsetToMethodName = new Dictionary<string, NativeMethod>();

            using (var stream = File.OpenRead (indexFile))
            {
                using (var reader = new StreamReader (stream))
                {
                    string line;
                    string currentFile = "Unknown";

                    while ((line = reader.ReadLine ()) != null) {
                        if (line.StartsWith ("Name: ", StringComparison.InvariantCulture))
                        {
                            currentFile = line.Substring(7);
                            continue;
                        }

                        var fields = line.Split(' ');

                        var nm = new NativeMethod { FileName = currentFile, Name = fields[1] };
                        offsetToMethodName[fields[0]] = nm;
                    }
                }
            }
        }

        public void InitLLvmSymbolizer()
        {
            llvm_process = new Process();
            llvm_process.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/local/opt/llvm/bin/llvm-symbolizer",
                Arguments = String.Format("-obj={0} -pretty-print", MonoExePath),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            llvm_process.Start();
        }

        public void Shutdown()
        {
            if (llvm_process != null)
            {
                llvm_process.Kill();
            }
        }

        bool EnrichFromIndex (MonoStateUnmanagedFrame frame)
        {
            if (offsetToMethodName == null)
            {
                return false;
            }

            if (offsetToMethodName.TryGetValue (frame.address, out var nativeMethod))
            {
                frame.name = $"{nativeMethod.Name} - {nativeMethod.FileName}";
                return true;
            }

            return false;
        }

        public void Enrich(MonoStateUnmanagedFrame frame)
        {
            if (EnrichFromIndex (frame))
            {
                return;
            }

            if (string.IsNullOrEmpty (MonoExePath))
            {
                return;
            }

            if (frame.address == "outside mono-sgen")
                return;

            if (llvm_process == null)
                InitLLvmSymbolizer();

            llvm_process.StandardInput.WriteLine(frame.address);
            llvm_process.StandardInput.WriteLine();

            string output = llvm_process.StandardOutput.ReadLine();
            frame.name = output;

            // Read the blank line
            llvm_process.StandardOutput.ReadLine();
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    class CodeCollection 
    {
        class KeyConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
            {
                if (sourceType == typeof (string))
                {
                    return true;
                }
                return base.CanConvertFrom(context, sourceType);
            }

            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
            {
                if (value is string v)
                {
                    return new Key(v);
                }
                return base.ConvertFrom(context, culture, value);
            }

            public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
            {
                if (destinationType == typeof (string))
                {
                    return value.ToString();
                }
                return base.ConvertTo(context, culture, value, destinationType);
            }
        }

        [TypeConverter (typeof (KeyConverter))]
        class Key : IEquatable<Key>
        {
            public string Mvid { get; set; }
            public uint Token { get; set; }

            public Key ()
            { }

            public Key (string serialized)
            {
                var v = serialized.Split(':');
                if (v.Length != 2)
                {
                    throw new Exception($"Invalid serialized format: {serialized}");
                }

                Mvid = v[0];
                Token = Convert.ToUInt32(v[1]);
            }

            public override string ToString()
            {
                return $"{Mvid}:{Token}";
            }

            public bool Equals (Key other)
            {
                if (other == null) { return false; }
                return Token == other.Token && string.Equals(Mvid, other.Mvid);
            }

            public override int GetHashCode()
            {
                unchecked // Overflow is fine, just wrap
                {
                    int hash = 12277;

                    hash = hash * 1372981 + Mvid.GetHashCode();
                    hash = hash * 1372981 + Token.GetHashCode();
                    return hash;
                }
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as Key);
            }
        }

        class Sequence
        {
            public string Filename { get; set; }
            public int Offset { get; set; }
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public int StartColumn { get; set; }
            public int EndColumn { get; set; }

            public Sequence ()
            {
            }

            public Sequence (SequencePoint sp)
            {
                Filename = sp.Document.Url;
                Offset = sp.Offset;
                StartLine = sp.StartLine;
                EndLine = sp.EndLine;
                StartColumn = sp.StartColumn;
                EndColumn = sp.EndColumn;
            }
        }

        class MethodType
        {
            public string Assembly { get; set; }
            public string Class { get; set; }
            public string Function { get; set; }
        }

        [JsonProperty]
        Dictionary<Key, List<Sequence>> Lookup { get; set; }
        [JsonProperty]
        Dictionary<Key, MethodType> Types { get; set; }

        Dictionary<string, HashSet<uint>> mvids = new Dictionary<string, HashSet<uint>>();

        public void Add (string assembly, string klass, string function, string mvid, uint token, Collection<SequencePoint> seqs)
        {
            var key = new Key
            {
                Mvid = mvid,
                Token = token
            };

            if (!mvids.TryGetValue(mvid, out HashSet<uint> tokens))
            {
                tokens = new HashSet<uint>();
                mvids[mvid] = tokens;
            }
            tokens.Add(token);

            List<Sequence> sequences = new List<Sequence>();
            foreach (var s in seqs)
            {
                sequences.Add(new Sequence(s));
            }

            Lookup[key] = sequences;
            Types[key] = new MethodType
            {
                Assembly = assembly,
                Class = klass,
                Function = function
            };
        }

        public CodeCollection ()
        {
            Lookup = new Dictionary<Key, List<Sequence>>();
            Types = new Dictionary<Key, MethodType>();
        }

        public bool TryEnrich(MonoStateManagedFrame frame)
        {
            var method_idx = new Key {
                Mvid = frame.mvid,
                Token = frame.token
            };

            if (!Lookup.ContainsKey(method_idx))
            {
                return false;
            }

            var seqs = Lookup [method_idx];

            uint goal = frame.offset;

            foreach (var seq in seqs)
            {
                if (goal != seq.Offset)
                    continue;

                frame.start_line = seq.StartLine;
                frame.start_col = seq.StartColumn;
                frame.end_line = seq.EndLine;
                frame.end_col = seq.EndColumn;
                frame.file = seq.Filename;
                break;
            }

            var typ = Types[method_idx];
            frame.assembly = typ.Assembly;
            frame.klass = typ.Class;
            frame.function = typ.Function;

            // Console.WriteLine("Made frame: {0} {1} {2} {3} {4}", frame.assembly, frame.klass, frame.function, frame.file, frame.start_line);

            return true;
        }

        public void Serialize (string filename)
        {
            using (var stream = File.Create (filename + ".gz"))
            {
                using (var compressedStream = new GZipStream(stream, CompressionMode.Compress))
                {
                    using (var streamWriter = new StreamWriter(compressedStream))
                    {
                        var serializer = new JsonSerializer();
                        serializer.Serialize(streamWriter, this);
                    }
                }
            }
        }

        public static CodeCollection Deserialize (string filename)
        {
            using (var stream = File.OpenRead (filename))
            {
                using (var decompressedStream = new GZipStream (stream, CompressionMode.Decompress))
                {
                    using (var streamReader = new StreamReader(decompressedStream))
                    {
                        var serializer = new JsonSerializer();

                        var reader = new JsonTextReader(streamReader);
                        return serializer.Deserialize<CodeCollection>(reader);
                    }
                }
            }
        }
    }

    class SymbolicationRequest
    {
        public readonly MonoStateThread[] Threads;
        public NativeMapper NativeMapper { get; set; }

        public static MonoStateFrame []
        ParseFrames (JArray frames)
        {
            if (frames == null)
                return Array.Empty<MonoStateFrame> ();

            var output = new MonoStateFrame[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = ((JObject) frames[i]);

                if (!frame.ContainsKey("is_managed") || (string)(frame["is_managed"]) != "true") {
                    var added = new MonoStateUnmanagedFrame();
                    added.address = (string)frame["native_address"];

                    output [i] = added;
                } else {
                    var added = new MonoStateManagedFrame();
                    added.mvid = (string) frame ["guid"];
                    added.token = Convert.ToUInt32 ((string) frame ["token"], 16);
                    added.offset = Convert.ToUInt32 ((string) frame ["il_offset"], 16);
                    output[i] = added;
                }
            }
            return output;
        }

        public SymbolicationRequest (JObject input, NativeMapper mapper) {
            var payload = input["payload"];
            var version = payload["protocol_version"];
            var crash_threads = (JArray)payload["threads"];

            NativeMapper = mapper;

            Threads = new MonoStateThread [crash_threads.Count];

            for (int i = 0; i < this.Threads.Length; i++)
            {
                var thread = ((JObject) crash_threads [i]);

                var managed_frames = SymbolicationRequest.ParseFrames((JArray) thread ["managed_frames"]);
                var unmanaged_frames = SymbolicationRequest.ParseFrames((JArray)thread["unmanaged_frames"]);

                var name = "";
                Threads[i] = new MonoStateThread(managed_frames, unmanaged_frames, name);
            }
        }

        public void Process (List<CodeCollection> maps)
        {
            foreach (var thread in Threads)
            {
                if (thread == null)
                    continue;
                
                foreach (var frame in thread.ManagedFrames)
                    ProcessOne (maps, frame);

                foreach (var frame in thread.NativeFrames)
                    ProcessOne(maps, frame);
            }
        }

        public void ProcessOne (List<CodeCollection> maps, MonoStateFrame frame)
        {
            if (frame is MonoStateManagedFrame managedFrame)
            {
                foreach (var map in maps)
                {
                    if (map.TryEnrich(managedFrame))
                    {
                        break;
                    }
                }
            }
            else if (frame is MonoStateUnmanagedFrame unmanagedFrame)
            {
                NativeMapper.Enrich(unmanagedFrame);
            }

            return;
        }

        public void Emit(string filename)
        {
            var streamWriter = new StreamWriter (filename);

            using (JsonWriter writer = new JsonTextWriter(streamWriter))
            {
                writer.Formatting = Formatting.Indented;

                writer.WriteStartObject();
                writer.WritePropertyName("Threads");
                writer.WriteStartArray();

                foreach (var thread in Threads)
                    if (thread != null)
                        thread.Emit(writer);

                writer.WriteEnd();
                writer.WriteEndObject();
            }
        }
    }

    class Symbolicator
    {
        static Stopwatch stopwatch = new Stopwatch ();

        public static string FormatFrame(MonoStateFrame frame)
        {
            //Console.WriteLine ("Frame: {0}", frame.);
            return "";
        }


        public Symbolicator()
        {
        }

        static readonly ReaderParameters readerParameters = new ReaderParameters { ReadSymbols = true };
        static readonly ReaderParameters readerParametersNoSymbols = new ReaderParameters { ReadSymbols = false };

        // Here is where we put any workarounds for file format issues
        public static JObject TryReadJson (string filePath)
        {
            var allText = File.ReadAllText(filePath);

            JObject crashFile = null;
            try
            {
                crashFile = JObject.Parse(allText);
            }
            catch (JsonReaderException)
            {
                // This is a fix for version 1.0 of the merp crash dump format

                var cleaned = Regex.Replace (allText, "\\n    \"EventType:\"", ",\n    \"EventType:\"");
                try {
                    crashFile = JObject.Parse(cleaned);
                } catch (JsonReaderException) {
                    throw new Exception("Could not parse input file even with workarounds for known issues");
                }
            }

            return crashFile;
        }

        static void GetAllAssemblies (string path, CodeCollection mapping)
        {
            foreach (var s in Directory.EnumerateFiles (path, "*", SearchOption.AllDirectories)) {
                if (s.EndsWith (".exe", StringComparison.Ordinal) || s.EndsWith (".dll", StringComparison.Ordinal)) {
                    ParseAssembly (s, mapping);
                }
            }
        }

        static void ParseAssembly (string assemblyPath, CodeCollection mapping)
        {
            AssemblyDefinition myLibrary = null;
            try
            {
                var readerParams = File.Exists(Path.ChangeExtension(assemblyPath, ".pdb")) ? readerParameters : readerParametersNoSymbols;
                myLibrary = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error parsing assembly {1}: {0}", e.Message, assemblyPath);
                return;
            }

            string mvid = myLibrary.MainModule.Mvid.ToString().ToUpper();

            foreach (var ty in myLibrary.MainModule.Types)
            {
                for (int i = 0; i < ty.Methods.Count; i++)
                {
                    string klass = ty.FullName;
                    string function = ty.Methods[i].FullName;
                    uint token = Convert.ToUInt32(ty.Methods[i].MetadataToken.ToInt32());
                    mapping.Add(assemblyPath, klass, function, mvid, token, ty.Methods[i].DebugInformation.SequencePoints);
                }
            }
            myLibrary.Dispose();
        }

        static CodeCollection CreateMappingForPath (string path, string monoPath)
        {
            var mapping = new CodeCollection();

            GetAllAssemblies(path, mapping);

            return mapping;
        }

        static List<CodeCollection> FindAssemblies (string vsPath, string monoPath, string monoExePath, string vsIndex, string monoIndex)
        {
            var maps = new List<CodeCollection>();

            if (string.IsNullOrEmpty(vsIndex))
            {
                maps.Add(CreateMappingForPath(vsPath, monoExePath));
            }
            else
            {
                maps.Add(CodeCollection.Deserialize (vsIndex));
            }

            if (string.IsNullOrEmpty(monoIndex))
            {
                maps.Add(CreateMappingForPath(monoPath, monoExePath));
            }
            else
            {
                maps.Add(CodeCollection.Deserialize (monoIndex));
            }

            var home = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
            var addin = Path.Combine (home, "Library/Application Support/VisualStudio/7.0/LocalInstall/Addins");
            if (Directory.Exists (addin)) {
                maps.Add(CreateMappingForPath(addin, monoExePath));
            }

            return maps;
        }

        public static void Main(string[] args)
        {
            var outputFile = "CrashReportSymbolicated.json";
            string vsFolder = null;
            string vsIndex = null;
            string monoPrefix = null;
            string monoIndex = null;
            string crashPath = null;
            string indexFile = null;
            string nativeIndex = null;
            bool shouldShowHelp = false;

            var options = new OptionSet {
                { "crashFile=", "The path to CrashReport.txt", n => crashPath = n },
                { "outputFile=", "The filename of the symbolicated crash report", n => outputFile = n },
                { "vsmacPath=", "The path to the VSMac folder", n => vsFolder = n },
                { "vsmacIndex=", "The path to the VSMac symbol index file", n => vsIndex = n },
                { "monoPath=", "The path to the Mono folder", n => monoPrefix = n },
                { "monoIndex=", "The path to the Mono symbol index file", n => monoIndex = n },
                { "generateIndexFile=", "The filename of the index file", n => indexFile = n },
                { "monoNativeIndex=", "The managed symbol index file", n => nativeIndex = n },
                { "help", "Print help", n => shouldShowHelp = n != null }
            };

            List<string> extra;
            try
            {
                extra = options.Parse (args);
            }
            catch (OptionException e)
            {
                Console.Write("DumpSymbolicate.exe: ");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `DumpSymbolicate.exe --help' for more information.");
                return;
            }

            if (shouldShowHelp) {
                ShowHelp (options);
                return;
            }

            stopwatch.Start();

            string monoPath = null;
            if (!string.IsNullOrEmpty(monoPrefix))
            {
                monoPath = Path.Combine(monoPrefix, "bin", "mono");
            }
            var nativeMapper = new NativeMapper(nativeIndex) { MonoExePath = monoPath };

            SymbolicationRequest request = null;
            long readingJson = 0, createRequest = 0;
            if (!string.IsNullOrEmpty(crashPath))
            {
                var crashFile = TryReadJson(crashPath);
                readingJson = stopwatch.ElapsedMilliseconds;

                stopwatch.Restart();
                request = new SymbolicationRequest(crashFile, nativeMapper);
                createRequest = stopwatch.ElapsedMilliseconds;
            }

            // Only load assemblies for which we have debug info
            stopwatch.Restart ();
            var maps = FindAssemblies(vsFolder, monoPrefix, monoPath, vsIndex, monoIndex);
            var processAssemblies = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            if (!string.IsNullOrEmpty(indexFile)) {
                maps[0].Serialize(indexFile + "-vsmac.json");
                maps[1].Serialize(indexFile + "-mono.json");
            }
            var creatingIndex = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart ();

            long symbolicate = 0;
            if (request != null)
            {
                request.Process(maps);
                symbolicate = stopwatch.ElapsedMilliseconds;

                request.Emit(outputFile);
            }

            nativeMapper.Shutdown ();

            stopwatch.Stop ();

            Console.WriteLine ("Timings\n-------");
            Console.WriteLine ($"   Reading crash log: {readingJson}ms");
            Console.WriteLine ($"   Creating request: {createRequest}ms");
            Console.WriteLine ($"   Processing assemblies: {processAssemblies}ms");
            Console.WriteLine ($"   Writing indexes: {creatingIndex}ms");
            Console.WriteLine ($"   Symbolification: {symbolicate}ms");

            //var MonoState = new MonoStateParser (argv [1]);
            //foreach (var thread in MonoState.Threads)
            //foreach (var frame in thread.Frames)
            //FormatFrame (frame);

            // AppDomain.Unload(safe_domain);
        }

        static void ShowHelp (OptionSet options)
        {
            Console.WriteLine("Usage: DumpSymbolicate.exe [OPTIONS]");
            Console.WriteLine("Symbolicate a VSMac crash report");
            Console.WriteLine();
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }
    }
}
