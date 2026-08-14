using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DeepSeekHarnessManager
{
    public static class JsonStore
    {
        private static readonly JavaScriptSerializer Serializer = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 4 * 1024 * 1024;
            serializer.RecursionLimit = 64;
            return serializer;
        }

        public static T Read<T>(string path) where T : class
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return Serializer.Deserialize<T>(json);
        }

        public static void Write<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string json = Serializer.Serialize(value);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, PrettyPrint(json), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                try { File.Replace(temporary, path, backup, true); }
                catch
                {
                    try { File.Copy(path, backup, true); } catch { }
                    File.Delete(path);
                    File.Move(temporary, path);
                }
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            return Serializer.Deserialize<T>(json);
        }

        private static string PrettyPrint(string json)
        {
            StringBuilder output = new StringBuilder();
            bool quoted = false;
            bool escaped = false;
            int indent = 0;
            int i;
            for (i = 0; i < json.Length; i++)
            {
                char value = json[i];
                if (quoted)
                {
                    output.Append(value);
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') quoted = false;
                    continue;
                }
                if (value == '"')
                {
                    quoted = true;
                    output.Append(value);
                }
                else if (value == '{' || value == '[')
                {
                    output.Append(value).AppendLine();
                    indent++;
                    output.Append(new string(' ', indent * 2));
                }
                else if (value == '}' || value == ']')
                {
                    output.AppendLine();
                    indent--;
                    output.Append(new string(' ', indent * 2)).Append(value);
                }
                else if (value == ',')
                {
                    output.Append(value).AppendLine();
                    output.Append(new string(' ', indent * 2));
                }
                else if (value == ':')
                {
                    output.Append(": ");
                }
                else if (!Char.IsWhiteSpace(value))
                {
                    output.Append(value);
                }
            }
            return output.ToString() + Environment.NewLine;
        }
    }
}
