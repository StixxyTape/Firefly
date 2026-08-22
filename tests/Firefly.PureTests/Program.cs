using System;
using System.Collections.Generic;

namespace Firefly
{
    internal static class Program
    {
        private static int assertions;

        private static int Main()
        {
            try
            {
                TestJsonCleanup();
                TestIntents();
                TestParameters();
                Console.WriteLine($"Firefly.PureTests passed {assertions} assertions.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Firefly.PureTests failed: " + error.Message);
                return 1;
            }
        }

        private static void TestJsonCleanup()
        {
            Equal("trim plain JSON", "{\"a\":1}", JsonResponseCore.ExtractJson("  {\"a\":1}  "));
            Equal("strip JSON fence", "{\"a\":1}", JsonResponseCore.ExtractJson("```json\n{\"a\":1}\n```"));
            Equal("strip unterminated fence", "{\"a\":1}", JsonResponseCore.ExtractJson("```json\n{\"a\":1}"));
            NotNull("parse valid object", JsonResponseCore.ParseObject("{\"a\":1}"));
            NotNull("repair missing object close", JsonResponseCore.ParseObject("{\"a\":1"));
            NotNull("repair nested closes", JsonResponseCore.ParseObject("{\"a\":[{\"b\":2"));
            Null("reject unterminated string", JsonResponseCore.ParseObject("{\"a\":\"broken}"));
            Null("reject excessive repairs", JsonResponseCore.ParseObject("{\"a\":[[[[1"));
        }

        private static void TestIntents()
        {
            var threads = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thread-1" };
            EventIntentResult existing = ValidIntent(
                "{\"intent\":\" a \",\"thread_id\":\"thread-1\",\"reason\":\" fits \"}", threads);
            Equal("intent A", EventIntent.ExistingThread, existing.Intent);
            Equal("trim intent reason", "fits", existing.Reason);
            Equal("intent B", EventIntent.NewThreadMaterial,
                ValidIntent("{\"intent\":\"B\",\"reason\":\"new\"}", threads).Intent);
            Equal("fenced intent C", EventIntent.Background,
                ValidIntent("```json\n{\"intent\":\"C\",\"reason\":\"background\"}\n```", threads).Intent);
            InvalidIntent("missing reason", "{\"intent\":\"C\"}", threads);
            InvalidIntent("unknown intent", "{\"intent\":\"D\",\"reason\":\"x\"}", threads);
            InvalidIntent("unknown thread", "{\"intent\":\"A\",\"thread_id\":\"missing\",\"reason\":\"x\"}", threads);
            InvalidIntent("non-A thread forbidden", "{\"intent\":\"B\",\"thread_id\":\"thread-1\",\"reason\":\"x\"}", threads);
        }

        private static void TestParameters()
        {
            var intent = new EventIntentResult
                { Intent = EventIntent.ExistingThread, ThreadId = "thread-1", Reason = "fits" };
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "points", "faction" };
            EventDecisionResult valid = ValidParameters(
                "{\"custom_letter_text\":\"Before {BASETEXT} after\",\"fields\":{\"POINTS\":\" 120 \"}}",
                intent, fields);
            Equal("canonical field name", "120", valid.ProposedValues["points"]);
            True("valid parameters intervene", valid.HasIntervention);
            InvalidParameters("missing base marker", "{\"custom_letter_text\":\"plain\",\"fields\":{}}", intent, fields);
            InvalidParameters("duplicate base marker", "{\"custom_letter_text\":\"{BASETEXT} {BASETEXT}\",\"fields\":{}}", intent, fields);
            InvalidParameters("unknown field", "{\"custom_letter_text\":\"{BASETEXT}\",\"fields\":{\"weather\":\"rain\"}}", intent, fields);
            InvalidParameters("non-string field", "{\"custom_letter_text\":\"{BASETEXT}\",\"fields\":{\"points\":120}}", intent, fields);
            InvalidParameters("empty field", "{\"custom_letter_text\":\"{BASETEXT}\",\"fields\":{\"points\":\"  \"}}", intent, fields);
            InvalidParameters("missing fields object", "{\"custom_letter_text\":\"{BASETEXT}\"}", intent, fields);
        }

        private static EventIntentResult ValidIntent(string json, ISet<string> threads)
        {
            EventIntentResult result = EventDecisionResponseParserCore.ParseIntent(json, threads, out string error);
            if (result == null) throw new Exception("expected valid intent: " + error);
            return result;
        }

        private static void InvalidIntent(string name, string json, ISet<string> threads) =>
            Null(name, EventDecisionResponseParserCore.ParseIntent(json, threads, out _));

        private static EventDecisionResult ValidParameters(string json, EventIntentResult intent,
            ISet<string> fields)
        {
            EventDecisionResult result = EventDecisionResponseParserCore.ParseParameters(
                json, intent, fields, out string error);
            if (result == null) throw new Exception("expected valid parameters: " + error);
            return result;
        }

        private static void InvalidParameters(string name, string json, EventIntentResult intent,
            ISet<string> fields) => Null(name,
                EventDecisionResponseParserCore.ParseParameters(json, intent, fields, out _));

        private static void Equal<T>(string name, T expected, T actual)
        {
            assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{name}: expected '{expected}', got '{actual}'");
        }

        private static void NotNull(string name, object value)
        {
            assertions++;
            if (value == null) throw new Exception(name + ": expected a value");
        }

        private static void Null(string name, object value)
        {
            assertions++;
            if (value != null) throw new Exception(name + ": expected null");
        }

        private static void True(string name, bool value)
        {
            assertions++;
            if (!value) throw new Exception(name + ": expected true");
        }
    }
}
