using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.PersistentSync;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LmpCommonTest
{
    /// <summary>
    /// The game client decodes compressed PersistentSync snapshots, and it does so with the same older
    /// System.Runtime.CompilerServices.Unsafe (4.5.3) and System.Memory (4.5.4) these tests reference, not the 6.0 and 4.5.5
    /// ZstdSharp was built against. This project runs on .NET Framework 4.7.2, the closest stand-in for KSP's Mono available
    /// without KSP, and decodes frames made offline by a current ZstdSharp. The server (a current runtime) is what compresses.
    /// </summary>
    [TestClass]
    public class SnapshotClientRuntimeTests
    {
        // Frames made with ZstdSharp 0.8.5 level 3 from Payload(false) / Payload(true): the plain frame, and the changed
        // payload compressed with the first as dictionary.
        private const string PlainFrame = "KLUv/WDaIK0lAMpLbAshQG2SDu9DY170ViBIIuT5v2QorqO1bLKbDDYgjOKC4BUB0gCgAKAAp5USh3qeP0XcJnaS9JxD0AliqlqjJi3R8YedppwjlSAxk7Qzqa6OSI120NiW17CY5iM/61v2rIynYxijQQr6GaOx1cpoSDA4CBDhHxogIPgtaET4nnyym7P5hj+ngMMnSDQMA4zvVLD7zWMxoZiAKD7xDB40cFAYBCCKxALSUKDBYEAgCgmIYsDCgQUDiKIKw3gwCw6GAcJBFP+wIFEWEiPhgCgS7+Cw0PAPDsofUACg8A8iJBBFVRWZY85MlwIABbMAggJEIaFIEGDhQAICBQIEZmWE21puW9nZjyIfyvvzVrpa5afmSR+H2TC0azuLTzs8s/LbLnmQdkO85/eY4X672OMU6lP3Zy5ZtJD/wGIsbbNU7QmhMeq/kLrUyzfHCJHdtqIGbxN5WA8qVy7J8SzeM29mh682tzlqOv1gdkfcLrvi0XTWWGlz0o+UrqZCLM9c+ko9fSyTnV5jcy/jNlrK0pGvXKJ7f0vISVb1jM/4fD6MzldPGXM93NLjRqjlWPE7b94pq26UdiNPkfYmbaSGk39wdu+wNlo37ybzfFxhNmJFL+ULq9DxGLNGbY6HtMku7UIOrpGbN0Zu6Bpz21Cj4dk5ym6e7yeWGhEdx77Ih7ZsumlUrIx3VVeiGqNceBTv1Bx9aPRO7Gw6jz6jo1ZsX3X9sLwZ1fGm9h2yXX2akX0pzjvWaOWMMrpxxLGQVg92x2vGch5dj5NvuNCV2+ARZg02nI2mBwfiUdbmKGa5iVnF70gL+UKI0VRFM+bY0bb5GTpu+YYftSNOxhtnudgN10oddRmT7tjNZuXw6sjLNTfWNurJPkaj+wab8mT/jUh+KctuWN3cnW9UKvmo7ZlVm+J2Sg7djq2UMfo+JFMPX+692WJ0UpfxEMVVMk4hRmWkxnkvZmeyITEyU6QYaqVBDIDYqMEFhCArEAkkEj1tB7AiEQnGjB4S4CAIQZNECE44hKAioUIjOAIiIEKjEBL4AaFKwinqS7VK2n5OESmqtbZG2zpISMn0NhOIcIQXGW97YFC5VqjNHEqvQrv/I+0hS9tW45drB0jI2L65iq0h1bIfEzROW+YbISNhevtLEUlh0bJChJJoWygcwrtQs64teO21mr/R4lsaUgJ+O0ByiUzea2SO7ZbzxVpR+zuXrsls/lqnNf2JAoMs07YYtFh7MBlD+1MiAl+v9ba/xtJp097fSBnJ6W2vTYGSfhGF+CgQ8MjitnksXSi5b9MchJwLdagQQSS6LQgKIVdqlt4cYFt/LIShUrNetYAOxBGUEI5tzxhE3mVaA0WWNq8HIeSMcpsHyC+CbHQDYhEapPZEoRPNEoFNe+oMIn2d1m0x6ZrEszWQ6Ptj+3eVVkRE+0bYtB2MTtFqP5O7NGhhOYWsf9xn8/uDblYOGup6rLw1ZdqrMhbbPaT6rGD2bCKnkABEgh4Tak2Wqs+eTVSevY5O3x/buEjYyExbqqIMJPoNEY4GVW42utrVJj30OiJmmQsRRTQJ29qrRRZBLyr2GgnIsfZ6IVlHQefWD8fy/6k6sdtf+1Q3Ab0K";
        private const string DeltaFrame = "KLUv/WDOIN0BAAhDDgBRo0OtoLU8gmHTof5gWh7hr+pQf0gtjyDYdag/lJZH8Cs71B86yyPYtR3qD88EfhS6Q1VRklAg";
        private const uint BaseChecksum = 1564135539u;
        private const uint ChangedChecksum = 125341403u;

        static SnapshotClientRuntimeTests()
        {
            // KSP's Mono ignores assembly versions when binding. .NET Framework does not, and ZstdSharp references newer
            // versions of Unsafe and System.Memory than the client ships, so do what Mono does: bind by name.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        /// <summary>
        /// The test host (vstest) loads its own, newer Unsafe into this AppDomain, which would make every check here pass
        /// against the wrong DLL. A fresh AppDomain whose base is the test folder loads only what the client ships.
        /// </summary>
        [TestMethod]
        public void ZstdDecodesWithTheOlderDependenciesTheClientShips()
        {
            var baseDirectory = Path.GetDirectoryName(typeof(SnapshotClientRuntimeTests).Assembly.Location);

            // KSP's Mono binds by name and ignores versions. .NET Framework checks them, so redirect every requested version
            // of the four support assemblies to the exact DLLs the client ships (the ones in the test folder).
            var configPath = Path.Combine(Path.GetTempPath(), "vmp-client-runtime-" + Guid.NewGuid().ToString("N") + ".config");
            File.WriteAllText(configPath,
                "<configuration><runtime><assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">" +
                Redirect("System.Runtime.CompilerServices.Unsafe", "b03f5f7f11d50a3a", "4.0.4.1") +
                Redirect("System.Memory", "cc7b13ffcd2ddd51", "4.0.1.1") +
                Redirect("System.Buffers", "cc7b13ffcd2ddd51", "4.0.3.0") +
                Redirect("System.Numerics.Vectors", "b03f5f7f11d50a3a", "4.1.4.0") +
                "</assemblyBinding></runtime></configuration>");

            var domain = AppDomain.CreateDomain("client-runtime", null, new AppDomainSetup { ApplicationBase = baseDirectory, ConfigurationFile = configPath });
            try
            {
                var probe = (IsolatedClientRuntime)domain.CreateInstanceAndUnwrap(typeof(IsolatedClientRuntime).Assembly.FullName, typeof(IsolatedClientRuntime).FullName);
                var report = probe.Run(PlainFrame, DeltaFrame);

                var all = string.Join(", ", report.Select(kv => kv.Key + "=" + kv.Value));
                Assert.AreEqual("True", report["canDecompress"], "ZstdSharp self test failed: " + all);
                Assert.AreEqual("4.0.4.1", report["unsafe"], "the check must run against the client's Unsafe 4.5.3, not the newer one the test host loads. " + all);
                Assert.AreEqual("4.0.1.1", report["memory"], "and the client's System.Memory 4.5.4");
                Assert.AreEqual("True", report["canDecompress"], "ZstdSharp self test failed: " + report["error"]);
                Assert.AreEqual("True", report["plainDecoded"], report["error"]);
                Assert.AreEqual("True", report["deltaDecoded"], report["error"]);
                Assert.AreEqual((SnapshotEncodings.CapQuickLz | SnapshotEncodings.CapZstd | SnapshotEncodings.CapZstdDictionary).ToString(), report["capabilities"]);
            }
            finally
            {
                AppDomain.Unload(domain);
                File.Delete(configPath);
            }
        }

        private static string Redirect(string name, string publicKeyToken, string version)
        {
            return "<dependentAssembly><assemblyIdentity name=\"" + name + "\" publicKeyToken=\"" + publicKeyToken + "\" culture=\"neutral\" />" +
                   "<bindingRedirect oldVersion=\"0.0.0.0-99.0.0.0\" newVersion=\"" + version + "\" /></dependentAssembly>";
        }

        /// <summary>Runs in its own AppDomain (see the test above). Everything it returns is a string.</summary>
        public sealed class IsolatedClientRuntime : MarshalByRefObject
        {
            public System.Collections.Generic.Dictionary<string, string> Run(string plainFrame, string deltaFrame)
            {
                var report = new System.Collections.Generic.Dictionary<string, string>();
                var first = Payload(false);
                var second = Payload(true);
                report["canDecompress"] = ZstdBackend.CanDecompress.ToString();
                report["capabilities"] = SnapshotCodec.LocalDecodeCapabilities.ToString();

                var plain = Convert.FromBase64String(plainFrame);
                report["plainDecoded"] = (SnapshotCodec.TryDecode(SnapshotEncodings.Zstd, plain, plain.Length, first.Length, 0, 0, null, out var raw, out var error) &&
                                          raw.SequenceEqual(first)).ToString();

                var delta = Convert.FromBase64String(deltaFrame);
                var heldBase = SnapshotBase.Create(1, first, first.Length);
                report["deltaDecoded"] = (SnapshotCodec.TryDecode(SnapshotEncodings.ZstdDictionary, delta, delta.Length, second.Length, 1, heldBase.Checksum, heldBase, out raw, out var deltaError) &&
                                          raw.SequenceEqual(second)).ToString();
                report["error"] = string.Join("; ", new[] { error, deltaError, ZstdBackend.Error }.Where(e => !string.IsNullOrEmpty(e)));

                foreach (var name in new[] { "System.Runtime.CompilerServices.Unsafe", "System.Memory" })
                {
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                    report[name.EndsWith("Unsafe", StringComparison.Ordinal) ? "unsafe" : "memory"] = loaded == null ? "not loaded" : loaded.GetName().Version.ToString();
                }

                return report;
            }
        }

        [TestMethod]
        public void PayloadGeneratorMatchesTheOneThatMadeTheFrames()
        {
            // If this fails the frames below were made from different text and every decode test is meaningless.
            Assert.AreEqual(BaseChecksum, SnapshotCodec.Checksum(Payload(false), Payload(false).Length));
            Assert.AreEqual(ChangedChecksum, SnapshotCodec.Checksum(Payload(true), Payload(true).Length));
        }

        [TestMethod]
        public void DecodesAPlainZstdFrameMadeByTheServer()
        {
            var expected = Payload(false);
            var frame = Convert.FromBase64String(PlainFrame);

            Assert.IsTrue(SnapshotCodec.TryDecode(SnapshotEncodings.Zstd, frame, frame.Length, expected.Length, 0, 0, null, out var raw, out var error), error);
            CollectionAssert.AreEqual(expected, raw);
        }

        [TestMethod]
        public void DecodesARelativeFrameAgainstTheSnapshotItWasMadeFrom()
        {
            var first = Payload(false);
            var second = Payload(true);
            var delta = Convert.FromBase64String(DeltaFrame);
            var heldBase = SnapshotBase.Create(11, first, first.Length);

            Assert.IsTrue(SnapshotCodec.TryDecode(SnapshotEncodings.ZstdDictionary, delta, delta.Length, second.Length, 11, heldBase.Checksum, heldBase, out var raw, out var error), error);
            CollectionAssert.AreEqual(second, raw);
            Assert.IsTrue(delta.Length < second.Length / 50, "the relative frame is tiny: " + delta.Length);
        }

        [TestMethod]
        public void ClientDecoderReadsTheServersFramesInArrivalOrderAndRefusesAMissingBase()
        {
            var first = Payload(false);
            var second = Payload(true);
            var decoder = new ClientSnapshotDecoder();

            var firstMessage = Message(1, SnapshotEncodings.Zstd, Convert.FromBase64String(PlainFrame), first.Length, 0, 0);
            Assert.IsTrue(decoder.TryDecodeInPlace(firstMessage, out var error), error);
            Assert.AreEqual(SnapshotEncodings.Raw, firstMessage.PayloadEncoding);
            CollectionAssert.AreEqual(first, firstMessage.Payload.Take(firstMessage.NumBytes).ToArray());
            Assert.AreEqual(BaseChecksum, decoder.GetBase(PersistentSyncDomainId.Contracts).Checksum);

            var secondMessage = Message(2, SnapshotEncodings.ZstdDictionary, Convert.FromBase64String(DeltaFrame), second.Length, 1, BaseChecksum);
            Assert.IsTrue(decoder.TryDecodeInPlace(secondMessage, out error), error);
            CollectionAssert.AreEqual(second, secondMessage.Payload.Take(secondMessage.NumBytes).ToArray());
            Assert.AreEqual(ChangedChecksum, decoder.GetBase(PersistentSyncDomainId.Contracts).Checksum, "the newest snapshot is the base now");

            // The same relative frame again, but the client holds the newer snapshot: refused, not misapplied.
            var repeat = Message(3, SnapshotEncodings.ZstdDictionary, Convert.FromBase64String(DeltaFrame), second.Length, 1, BaseChecksum);
            Assert.IsFalse(decoder.TryDecodeInPlace(repeat, out error));
            StringAssert.Contains(error, "mismatch");

            // A client that never received the first one has nothing to decode it against.
            var fresh = new ClientSnapshotDecoder();
            Assert.IsFalse(fresh.TryDecodeInPlace(Message(2, SnapshotEncodings.ZstdDictionary, Convert.FromBase64String(DeltaFrame), second.Length, 1, BaseChecksum), out error));
            StringAssert.Contains(error, "no base");
        }

        [TestMethod]
        public void QuickLzFallbackWorksOnTheClientRuntime()
        {
            var raw = Payload(false);
            var encoded = SnapshotCodec.Encode(raw, raw.Length, SnapshotEncodings.CapQuickLz, null);

            Assert.AreEqual(SnapshotEncodings.QuickLz, encoded.Encoding);
            Assert.IsTrue(SnapshotCodec.TryDecode(encoded.Encoding, encoded.Data, encoded.Length, encoded.RawLength, 0, 0, null, out var back, out var error), error);
            CollectionAssert.AreEqual(raw, back);
        }

        [TestMethod]
        public void DamagedFramesAreRefusedWithoutThrowingOnTheClientRuntime()
        {
            var expected = Payload(false);
            var frame = Convert.FromBase64String(PlainFrame);
            for (var i = 20; i < frame.Length; i += 11) frame[i] ^= 0x5A;

            Assert.IsFalse(SnapshotCodec.TryDecode(SnapshotEncodings.Zstd, frame, frame.Length, expected.Length, 0, 0, null, out var raw, out var error));
            Assert.IsNull(raw);
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }

        private static PersistentSyncSnapshotMsgData Message(long revision, byte encoding, byte[] wire, int rawLength, long baseRevision, uint baseChecksum)
        {
            return new PersistentSyncSnapshotMsgData
            {
                DomainId = PersistentSyncDomainId.Contracts,
                Revision = revision,
                PayloadEncoding = encoding,
                RawLength = rawLength,
                BaseRevision = baseRevision,
                BaseChecksum = baseChecksum,
                Payload = wire,
                NumBytes = wire.Length
            };
        }

        /// <summary>Same text as the offline generator: a pure function of the loop index, so it is identical on every runtime.</summary>
        private static byte[] Payload(bool changed)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 60; i++)
            {
                sb.Append("CONTRACT\n{\n\tguid = ").Append(((uint)(i * 7919 + 13) * 2654435761u).ToString("x8")).Append('-').Append((i * 40503).ToString("x4")).Append('\n');
                sb.Append("\ttype = ").Append(i % 3 == 0 ? "RecoverAsset" : i % 3 == 1 ? "PartTest" : "SurveyContract").Append('\n');
                sb.Append("\tstate = ").Append(i % 4 == 0 ? "Active" : "Offered").Append('\n');
                sb.Append("\tfundsAdvance = ").Append((i * 7919) % 100000).Append('\n');
                sb.Append("\tPARAM\n\t{\n\t\tname = Orbit\n\t\tstate = ").Append(changed && i % 10 == 3 ? "Complete" : "Incomplete").Append("\n\t}\n}\n");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }
}
