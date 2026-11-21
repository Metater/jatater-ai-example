using System;
using System.Collections.Generic;
using System.Text;

namespace JataterAI
{
    public sealed class Conversation
    {
        readonly List<Message> history = new List<Message>();
        readonly ConnectionSettings settings;
        readonly string system;
        readonly string greeting;

        public Conversation(ConnectionSettings settings, string personality, string greeting)
        {
            this.settings = settings;
            system = personality + " You are on the phone. Stay in character and answer in one short, speakable sentence.";
            this.greeting = greeting;
        }

        public Message[] Next(string input)
        {
            int overhead = Cost(system) + Cost(greeting) + Cost(input);
            int size = overhead;
            foreach (var m in history) size += Cost(m.content);
            if (settings.compactConversation && (history.Count / 2 >= Math.Max(1, settings.maxTurnPairs) || size >= settings.estimatedTokenLimit))
            {
                int keep = Math.Min(Math.Max(0, settings.retainTurnPairs), Math.Max(0, settings.maxTurnPairs - 1));
                int start = Math.Max(0, history.Count - keep * 2);
                size = overhead;
                for (int i = history.Count - 2; i >= start; i -= 2)
                {
                    size += Cost(history[i].content) + Cost(history[i+1].content);
                    if (size > settings.estimatedTokenLimit) { start = i+2; break; }
                }
                history.RemoveRange(0, start);
            }
            var messages = new List<Message>(history.Count + 3) { new Message("system", system), new Message("assistant", greeting) };
            messages.AddRange(history);
            messages.Add(new Message("user", input));
            return messages.ToArray();
        }

        public void Commit(string input, string reply)
        {
            history.Add(new Message("user", input));
            history.Add(new Message("assistant", reply));
        }
        static int Cost(string s) => 5 + (Encoding.UTF8.GetByteCount(s) + 2) / 3;
    }
}
