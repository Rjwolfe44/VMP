using LmpCommon.LogUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace LmpCommonTest
{
    [TestClass]
    public class LogSignatureTests
    {
        [TestMethod]
        public void NumbersAndIdsDoNotSplitASignature()
        {
            var a = LogSignature.Normalize("NullReferenceException: vessel 1234 part 5 at frame 98765");
            var b = LogSignature.Normalize("NullReferenceException: vessel 7 part 22 at frame 1");
            Assert.AreEqual(a, b);
            Assert.AreEqual("NullReferenceException: vessel # part # at frame #", a);
        }

        [TestMethod]
        public void GuidsAreCollapsed()
        {
            var a = LogSignature.Normalize("Vessel 3f2504e0-4f89-11d3-9a0c-0305e82c3301 not found");
            var b = LogSignature.Normalize("Vessel 11111111-2222-3333-4444-555555555555 not found");
            Assert.AreEqual(a, b);
            Assert.AreEqual("Vessel <guid> not found", a);
        }

        [TestMethod]
        public void OnlyTheFirstLineCounts()
        {
            Assert.AreEqual("Exception: boom", LogSignature.Normalize("Exception: boom\n  at Foo.Bar()\n  at Baz.Qux()"));
            Assert.AreEqual("Exception: boom", LogSignature.Normalize("Exception: boom\r\n  at Foo.Bar()"));
        }

        [TestMethod]
        public void WhitespaceIsTidiedAndLongTextIsCut()
        {
            Assert.AreEqual("a b c", LogSignature.Normalize("  a \t  b   c  "));
            var result = LogSignature.Normalize(new string('x', 5000));
            Assert.AreEqual(LogSignature.MaxLength, result.Length);
        }

        [TestMethod]
        public void EmptyInputGivesAnEmptySignature()
        {
            Assert.AreEqual(string.Empty, LogSignature.Normalize(null));
            Assert.AreEqual(string.Empty, LogSignature.Normalize(string.Empty));
            Assert.AreEqual(string.Empty, LogSignature.Normalize("\n\nfoo"));
        }

        [TestMethod]
        public void ADifferentErrorKeepsADifferentSignature()
        {
            Assert.AreNotEqual(LogSignature.Normalize("NullReferenceException at A"), LogSignature.Normalize("InvalidCastException at A"));
        }

        [TestMethod]
        public void ATruncatedGuidIsNotMistakenForOne()
        {
            var result = LogSignature.Normalize("id 3f2504e0-4f89-11d3-9a0c");
            Assert.IsFalse(result.Contains("<guid>"), result);
        }

        [TestMethod]
        public void ItIsFastEnoughForALogFlood()
        {
            var text = "NullReferenceException: Object reference not set to an instance of an object at Part 123456 vessel 3f2504e0-4f89-11d3-9a0c-0305e82c3301";
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 20000; i++) LogSignature.Normalize(text);
            watch.Stop();
            Assert.IsTrue(watch.ElapsedMilliseconds < 1500, $"20k signatures took {watch.ElapsedMilliseconds} ms");
        }
    }
}
