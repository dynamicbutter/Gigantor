using System;
using System.Threading;
using System.IO;
using NUnit.Framework;
using Imagibee.Gigantor;

namespace Testing {
    public class DuplicateCheckerTests {
        readonly string biblePath = Path.Combine("Assets", "BibleTest.txt");
        readonly string simplePath = Path.Combine("Assets", "SimpleTest.txt");
        readonly string simplePath2 = Path.Combine("Assets", "SimpleTest2.txt");

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void InitialStateTest()
        {
            DuplicateChecker checker = new("", "", new AutoResetEvent(false));
            Assert.AreEqual(false, checker.Running);
            Assert.AreEqual(false, checker.Identical);
            Assert.AreEqual(0, checker.ByteCount);
            Assert.AreEqual(true, checker.Error == "");
        }

        [Test]
        public void EmptyPathTest()
        {
            AutoResetEvent progress = new(false);
            DuplicateChecker checker = new("", "", progress);
            Assert.Throws<ArgumentException>(() => checker.Start());
        }

        [Test]
        public void MissingPathTest()
        {
            AutoResetEvent progress = new(false);
            DuplicateChecker checker = new(
                "A Missing File1", "A Missing File2", progress);
            Assert.Throws<FileNotFoundException>(() => checker.Start());
        }

        [Test]
        public void MatchingTest()
        {
            AutoResetEvent progress = new(false);
            DuplicateChecker checker = new(biblePath, biblePath, progress);
            checker.Start();
            Utilities.Wait(
                checker,
                progress,
                (_) => { },
                1000);
            Assert.AreEqual(true, checker.Error == "");
            Assert.AreEqual(true, checker.Identical);
        }

        public void SizeMismatchTest()
        {
            AutoResetEvent progress = new(false);
            DuplicateChecker checker = new(biblePath, simplePath, progress);
            checker.Start();
            Utilities.Wait(
                checker,
                progress,
                (_) => { },
                1000);
            Assert.AreEqual(true, checker.Error == "");
            Assert.AreEqual(false, checker.Identical);
        }

        public void ValueMismatchTest()
        {
            AutoResetEvent progress = new(false);
            DuplicateChecker checker = new(simplePath, simplePath2, progress);
            checker.Start();
            Utilities.Wait(
                checker,
                progress,
                (_) => { },
                1000);
            Assert.AreEqual(true, checker.Error == "");
            Assert.AreEqual(false, checker.Identical);
        }
    }
}

