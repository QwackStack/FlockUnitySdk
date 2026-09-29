using System;
using System.Collections.Generic;

namespace Flock.Logging
{
    /// <summary>Reads a game's exception without trusting it: a Message, StackTrace or ToString override that throws gets a fallback.</summary>
    internal static class FlockExceptionText
    {
        /// <summary>The exception's message, or a note naming what its Message getter threw.</summary>
        internal static string MessageOf(Exception exception)
        {
            try
            {
                return exception.Message;
            }
            catch (Exception failure)
            {
                return $"(its message could not be read: {failure.GetType().Name})";
            }
        }

        /// <summary>As Unity's log names an exception: its type, then its message.</summary>
        internal static string Describe(Exception exception) => $"{exception.GetType().Name}: {MessageOf(exception)}";

        /// <summary>The exception's stack, or empty when it has none or reading it throws.</summary>
        internal static string StackTraceOf(Exception exception)
        {
            try
            {
                return exception.StackTrace ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>The exception as its ToString writes it, or its description and stack when that throws.</summary>
        internal static string FullText(Exception exception)
        {
            try
            {
                return exception.ToString();
            }
            catch (Exception)
            {
                string stackTrace = StackTraceOf(exception);
                return stackTrace.Length > 0 ? Describe(exception) + "\n" + stackTrace : Describe(exception);
            }
        }

        /// <summary>The faults inside an aggregate, nested ones opened, in Flatten's order; reads no message, where Flatten does.</summary>
        internal static List<Exception> FaultsInside(AggregateException aggregate)
        {
            List<Exception> faults = new List<Exception>();
            List<AggregateException> toOpen = new List<AggregateException> { aggregate };
            for (int index = 0; index < toOpen.Count; index++)
            {
                foreach (Exception inner in toOpen[index].InnerExceptions)
                {
                    if (inner is AggregateException nested)
                        toOpen.Add(nested);
                    else if (inner != null)
                        faults.Add(inner);
                }
            }
            return faults;
        }

        /// <summary>The first <paramref name="most"/> characters, saying how long the text was when it is cut; never splits a character in two.</summary>
        internal static string Cut(string text, int most)
        {
            if (text == null)
                return string.Empty;
            if (text.Length <= most)
                return text;
            int keep = most;
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
                keep--;
            return text.Substring(0, keep) + $" [cut from {text.Length} characters]";
        }
    }
}
