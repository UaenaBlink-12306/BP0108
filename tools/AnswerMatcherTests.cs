using System;
using System.Collections.Generic;

namespace CodexRuntimePatch.Tests
{
    internal static class AnswerMatcherTests
    {
        private static int Main()
        {
            var failures = 0;
            failures += Check("diacritics and answer note", "Karl Donitz", "Karl Dönitz (be lenient on pronunciation)", new string[0], true);
            failures += Check("leading article", "the world", "The World", new string[0], true);
            failures += Check("alias", "Nippon", "Empire of Japan", new[] { "Nippon" }, true);
            failures += Check("punctuation", "mark-twain!", "Mark Twain", new string[0], true);
            failures += Check("near miss rejected", "Cuba Missle Crisis", "Cuban Missile Crisis", new string[0], false);
            failures += Check("partial answer rejected", "Twain", "Mark Twain", new string[0], false);

            Console.WriteLine(failures == 0 ? "Answer matcher tests passed." : failures + " answer matcher test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static int Check(string label, string message, string answer, IEnumerable<string> aliases, bool expected)
        {
            var actual = AnswerMatcher.Matches(message, answer, aliases);
            Console.WriteLine("{0}: expected={1}, actual={2}", label, expected, actual);
            return actual == expected ? 0 : 1;
        }
    }
}
