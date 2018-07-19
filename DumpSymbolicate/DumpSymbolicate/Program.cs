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

namespace DumpSymbolicate
{
    
    internal class MonoStateRuntime
    {
        internal string Version;

        internal MonoStateThread[] Threads;

        public MonoStateRuntime (string version, MonoStateThread [] threads)
        {
            Version = version;
            Threads = threads;
        }

        public void Emit ()
        {
            StringBuilder sb = new StringBuilder();
            StringWriter sw = new StringWriter(sb);

            using (JsonWriter writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.Indented;

                writer.WriteStartObject();
                writer.WritePropertyName("Version");
                writer.WriteValue(this.Version);
                writer.WritePropertyName("Threads");
                writer.WriteStartArray();

                foreach (var thread in Threads)
                    thread.Emit(writer);

                writer.WriteEnd();
                writer.WriteEndObject();
            }
        }
    }

    internal class MonoStateThread
    {
        // Only do managed frames for now
        internal MonoStateManagedFrame[] Frames;
        internal string Name;

        public MonoStateThread (MonoStateManagedFrame[] frames, string name)
        {
            if (frames == null)
                throw new Exception("Only non-null frames allowed for reporting");
            
            this.Frames = frames;
            this.Name = name;
        }

        public void Emit (JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Name");
            writer.WriteValue(this.Name);
            writer.WritePropertyName("Frames");
            writer.WriteStartArray();

            foreach (var frame in Frames)
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
    }

    internal class MonoStateUnmanagedFrame : MonoStateFrame
    {
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

        public void Emit(JsonWriter writer)
        {
            writer.WriteStartObject();

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
                   
            writer.WriteEndObject();
        }

    }

    class CodeCollection 
    {
        Dictionary<Tuple<string, uint>, Collection<SequencePoint>> Lookup;
        Dictionary<Tuple<string, uint>, Tuple<string, string, string>> Types;

        public void Add (string assembly, string klass, string function, string mvid, uint token, Collection<SequencePoint> seqs)
        {
            var key = new Tuple<string, uint>(mvid, token);
            Lookup[key] = seqs;
            Types[key] = new Tuple<string, string, string>(assembly, klass, function);
        }

        public CodeCollection ()
        {
            Lookup = new Dictionary<Tuple<string, uint>, Collection<SequencePoint>>();
            Types = new Dictionary<Tuple<string, uint>, Tuple<string, string, string>>();
        }

        public void Enrich (MonoStateManagedFrame frame)
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
            }

            var typ = Types[method_idx];
            frame.assembly = typ.Item1;
            frame.klass = typ.Item2;
            frame.function = typ.Item3;

            Console.WriteLine("Made frame: {0} {1} {2} {3} {4}", frame.assembly, frame.klass, frame.function, frame.file, frame.start_line);
        }
    }

    class SymbolicationRequest
    {
        public readonly MonoStateThread[] Threads;

        public static MonoStateManagedFrame []
        ParseFrames (JArray frames)
        {
            var output = new MonoStateManagedFrame[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = ((JObject) frames[i]);
                //Console.WriteLine (frame.ToString ());

                output [i] = new MonoStateManagedFrame ();

                if (!frame.ContainsKey("is_managed") || (string) (frame ["is_managed"]) != "true")
                    continue;

                output[i].mvid = (string) frame ["guid"];
                output[i].token = Convert.ToUInt32 ((string) frame ["token"], 16);
                output[i].offset = Convert.ToUInt32 ((string) frame ["il_offset"], 16);

                //Console.WriteLine("Parsed {0} {1:X} {2:X}", output [i].mvid, output[i].token, output[i].offset);
            }
            return output;
        }

        public SymbolicationRequest (JObject input) {
            var payload = input["payload"];
            var version = payload["protocol_version"];
            var crash_threads = (JArray)payload["threads"];

            Threads = new MonoStateThread [crash_threads.Count];

            for (int i = 0; i < this.Threads.Length; i++)
            {
                var thread = ((JObject) crash_threads [i]);
                if (!thread.ContainsKey("managed_frames"))
                    continue;

                var frames = SymbolicationRequest.ParseFrames((JArray) thread ["managed_frames"]);
                var name = "";
                Threads[i] = new MonoStateThread(frames, name);
            }
        }

        public void Process (CodeCollection code)
        {
            foreach (var thread in Threads)
            {
                if (thread == null)
                    continue;
                
                foreach (var frame in thread.Frames)
                {
                    if (frame.mvid != null)
                    {
                        code.Enrich(frame);
                    }
                }
            }
        }
    }

    class Symbolicator
    {
        Dictionary<string, Assembly> guid_lookup;

        public void LoadAssembly(Assembly curAssembly)
        {
            var attribute = (GuidAttribute)curAssembly.GetCustomAttributes(typeof(GuidAttribute), true)[0];
            var guid = attribute.Value;
            this.guid_lookup[guid] = curAssembly;
        }

        public static string FormatFrame(MonoStateFrame frame)
        {
            //Console.WriteLine ("Frame: {0}", frame.);
            return "";
        }


        public Symbolicator()
        {
            this.guid_lookup = new Dictionary<string, Assembly>();
        }

        public static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Symbolcation file not provided");

            if (!File.Exists(args[0]))
                throw new Exception(String.Format("Symbolcation file not found {0}", args[0]));

            if (args.Length < 2)
                throw new Exception("Symbolcation folder not provided");

            var outputStr = File.ReadAllText(args[0]);
            Console.WriteLine("Read in: {0}", outputStr);
            Console.ReadLine();
            var crashFile = JObject.Parse(outputStr);
            var request = new SymbolicationRequest(crashFile);

            var inputFolder = args[1];
            string[] assemblies = Directory.GetFiles(inputFolder);
            var self = new Symbolicator ();

            var mapping = new CodeCollection ();

            // AppDomain safe_domain = AppDomain.CreateDomain("SafeDomain");
            foreach (string assembly in assemblies)
            {
                if (assembly.EndsWith(".dll") || assembly.EndsWith(".exe")) 
                {
                    Console.WriteLine("Reading {0}", assembly);
                    var readerParameters = new ReaderParameters { ReadSymbols = true };
                    AssemblyDefinition myLibrary = null;
                    try {
                         myLibrary = AssemblyDefinition.ReadAssembly (assembly, readerParameters);
                    } catch (Exception e) {
                        Console.WriteLine("Error parsing assembly {1}: {0}", e.Message, assembly);
                        continue;
                    }

                    string mvid = myLibrary.MainModule.Mvid.ToString ();

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
                }
            }

            request.Process(mapping);

            //var MonoState = new MonoStateParser (argv [1]);
            //foreach (var thread in MonoState.Threads)
            //foreach (var frame in thread.Frames)
            //FormatFrame (frame);

            // AppDomain.Unload(safe_domain);
        }
    }
}
