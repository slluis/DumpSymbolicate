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

namespace DumpSymbolicate
{

    internal class MonoStateThread
    {
        // Only do managed frames for now
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

    class CodeCollection 
    {
        Dictionary<Tuple<string, uint>, Collection<SequencePoint>> Lookup;
        Dictionary<Tuple<string, uint>, Tuple<string, string, string>> Types;
        public readonly string Runtime;

        public void Add (string assembly, string klass, string function, string mvid, uint token, Collection<SequencePoint> seqs)
        {
            var key = new Tuple<string, uint>(mvid, token);
            Lookup[key] = seqs;
            Types[key] = new Tuple<string, string, string>(assembly, klass, function);
        }

        public CodeCollection (string unmanaged_mono)
        {
            Lookup = new Dictionary<Tuple<string, uint>, Collection<SequencePoint>>();
            Types = new Dictionary<Tuple<string, uint>, Tuple<string, string, string>>();
            Runtime = unmanaged_mono;
        }

        Process llvm_process;

        public void InitLLvmSymbolizer ()
        {
            llvm_process = new Process();
            llvm_process.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/local/opt/llvm/bin/llvm-symbolizer",
                Arguments = String.Format("-obj={0} -pretty-print", Runtime),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            llvm_process.Start();
        }

        public void Shutdown ()
        {
            llvm_process.Kill ();
        }

        public void Enrich (MonoStateUnmanagedFrame frame)
        {
            if (frame.address == "outside mono-sgen")
                return;
            else
                Console.WriteLine ("Symbolicating!");

            Console.WriteLine("Process started, sending {0}", frame.address);

            if (llvm_process == null)
                InitLLvmSymbolizer ();

            llvm_process.StandardInput.WriteLine(frame.address);
            llvm_process.StandardInput.WriteLine();

            Console.WriteLine("Reading...");

            string output = llvm_process.StandardOutput.ReadLine();
            frame.name = output;

            Console.WriteLine("{0} Before Wait -> Exited", output);

            // Read the blank line
            llvm_process.StandardOutput.ReadLine();
        }

        public void Enrich(MonoStateManagedFrame frame)
        {
            var method_idx = new Tuple<string, uint>(frame.mvid, frame.token);
            if (!Lookup.ContainsKey(method_idx))
            {
                Console.WriteLine("Missing information for {0} {1}", frame.mvid, frame.token);
                return;
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
                frame.file = seq.Document.Url;
                break;
            }

            var typ = Types[method_idx];
            frame.assembly = typ.Item1;
            frame.klass = typ.Item2;
            frame.function = typ.Item3;

            // Console.WriteLine("Made frame: {0} {1} {2} {3} {4}", frame.assembly, frame.klass, frame.function, frame.file, frame.start_line);
        }
    }

    class SymbolicationRequest
    {
        public readonly MonoStateThread[] Threads;

        public static MonoStateFrame []
        ParseFrames (JArray frames)
        {
            if (frames == null)
                return Array.Empty<MonoStateFrame> ();

            var output = new MonoStateFrame[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = ((JObject) frames[i]);
                //Console.WriteLine (frame.ToString ());

                if (!frame.ContainsKey("is_managed") || (string)(frame["is_managed"]) != "true") {
                    var added = new MonoStateUnmanagedFrame();
                    added.address = (string)frame["native_address"];
                    Console.WriteLine ("Native address: {0}", added.address);
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

        public SymbolicationRequest (JObject input) {
            var payload = input["payload"];
            var version = payload["protocol_version"];
            var crash_threads = (JArray)payload["threads"];

            Console.WriteLine("There were {0} threads", crash_threads.Count);

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

        public void Process (CodeCollection code)
        {
            foreach (var thread in Threads)
            {
                if (thread == null)
                    continue;
                
                foreach (var frame in thread.ManagedFrames)
                    ProcessOne (code, frame);
                foreach (var frame in thread.NativeFrames)
                    ProcessOne(code, frame);
            }
        }

        public void ProcessOne (CodeCollection code, MonoStateFrame frame)
        {
            if (frame is MonoStateManagedFrame)
                code.Enrich(frame as MonoStateManagedFrame);
            else if (frame is MonoStateUnmanagedFrame)
                code.Enrich(frame as MonoStateUnmanagedFrame);
        }

        public string Emit()
        {
            StringBuilder sb = new StringBuilder();
            StringWriter sw = new StringWriter(sb);

            using (JsonWriter writer = new JsonTextWriter(sw))
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

            return sb.ToString();
        }
    }

    class Symbolicator
    {
        static Stopwatch stopwatch = new Stopwatch ();
        static long readingJson;
        static long createRequest;
        static long findAssemblies;
        static long readingAssemblies;
        static long symbolicate;

        public static string FormatFrame(MonoStateFrame frame)
        {
            //Console.WriteLine ("Frame: {0}", frame.);
            return "";
        }


        public Symbolicator()
        {
        }

        // Here is where we put any workarounds for file format issues
        public static JObject TryReadJson (string filePath)
        {
            var allText = File.ReadAllText(filePath);
            // Console.WriteLine("Read in: {0}", outputStr);
            // Console.ReadLine();
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

        static List<string> GetAllAssemblies (string path)
        {
            var assemblies = new List<string> ();

            foreach (var s in Directory.EnumerateFiles (path, "*", SearchOption.AllDirectories)) {
                if (s.EndsWith (".exe", StringComparison.Ordinal) || s.EndsWith (".dll", StringComparison.Ordinal)) {
                    assemblies.Add (s);
                }
            }

            return assemblies;
        }

        static List<string> FindAssemblies (string vsPath, string monoPath)
        {
            var files = new List<string>();
           
            files.AddRange (GetAllAssemblies (vsPath));
            files.AddRange (GetAllAssemblies (monoPath));

            var home = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
            var addin = Path.Combine (home, "Library/Application Support/VisualStudio/7.0/LocalInstall/Addins");
            if (Directory.Exists (addin)) {
                files.AddRange(Directory.GetFiles(addin, "*.dll", SearchOption.AllDirectories));
            }

            return files;
        }

        public static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Symbolcation file not provided");

            if (!File.Exists(args[0]))
                throw new Exception(String.Format("Symbolcation file not found {0}", args[0]));

            Console.WriteLine ("Reading crash JSON");
            stopwatch.Start();
            var crashFile = Symbolicator.TryReadJson(args[0]);
            readingJson = stopwatch.ElapsedMilliseconds;

            Console.WriteLine ("Creating request");
            stopwatch.Restart ();
            var request = new SymbolicationRequest(crashFile);
            createRequest = stopwatch.ElapsedMilliseconds;

            if (args.Length < 2)
                throw new Exception("Symbolcation folder not provided");

            var vsFolder = args[1];

            if (args.Length < 3)
                throw new Exception("Unmanaged mono not given");

            var monoPrefix = args [2];
            var monoPath = Path.Combine(monoPrefix, "bin", "mono");

            // Only load assemblies for which we have debug info
            stopwatch.Restart ();
            var assemblies = FindAssemblies(vsFolder, monoPrefix);
            findAssemblies = stopwatch.ElapsedMilliseconds;

            Console.WriteLine ("Traversing {0} assemblies", assemblies.Count);

            var mapping = new CodeCollection (monoPath);

            var readerParameters = new ReaderParameters { ReadSymbols = true };
            var readerParametersNoSymbols = new ReaderParameters { ReadSymbols = false };

            // AppDomain safe_domain = AppDomain.CreateDomain("SafeDomain");
            stopwatch.Restart ();
            foreach (string assembly in assemblies)
            {
                AssemblyDefinition myLibrary = null;
                try
                {
                    var readerParams = File.Exists(Path.ChangeExtension(assembly, ".pdb")) ? readerParameters : readerParametersNoSymbols;
                    myLibrary = AssemblyDefinition.ReadAssembly(assembly, readerParams);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error parsing assembly {1}: {0}", e.Message, assembly);
                    continue;
                }

                string mvid = myLibrary.MainModule.Mvid.ToString ().ToUpper ();

                Console.WriteLine("{0} {1}", assembly, mvid);
                Console.WriteLine("Read {0}", assembly);

                foreach (var ty in myLibrary.MainModule.Types){
                    for (int i = 0; i < ty.Methods.Count; i++)
                    {
                        string klass = ty.FullName;
                        string function = ty.Methods[i].FullName;
                        uint token = Convert.ToUInt32 (ty.Methods[i].MetadataToken.ToInt32());
                        mapping.Add (assembly, klass, function, mvid, token, ty.Methods [i].DebugInformation.SequencePoints);
                    }
                }
                myLibrary.Dispose();
            }
            readingAssemblies = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart ();
            request.Process(mapping);
            var result = request.Emit();
            mapping.Shutdown ();

            stopwatch.Stop ();

            Console.WriteLine ("Timings\n-------");
            Console.WriteLine ($"   Reading crash log: {readingJson}ms");
            Console.WriteLine ($"   Creating request: {createRequest}ms");
            Console.WriteLine ($"   Finding assemblies: {findAssemblies}ms");
            Console.WriteLine ($"   Reading assemblies: {readingAssemblies}ms");
            Console.WriteLine ($"   Symbolification: {symbolicate}ms");

            //var MonoState = new MonoStateParser (argv [1]);
            //foreach (var thread in MonoState.Threads)
            //foreach (var frame in thread.Frames)
            //FormatFrame (frame);

            // AppDomain.Unload(safe_domain);
        }
    }
}
