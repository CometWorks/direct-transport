using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin;

// Plugin options read straight from the process command line.
//
// Environment.GetCommandLineArgs is process-wide, and Pulsar loads plugins into
// the game process and forwards its own arguments to the game, so the plugin
// sees the exact command line the launcher was started with. That removes the
// need for the launcher to hand options over through environment variables: an
// option meant for this plugin can simply be read here.
//
// Matching follows Pulsar's own convention (Pulsar.Shared.Flags), so an option
// spelled as words is accepted in the Linux form (--client-name), the Windows
// form (/ClientName) and the Space Engineers form (-clientName), all
// case-insensitively. Values are taken either from the next argument
// (--connect host:port) or from an inline assignment (--connect=host:port).
public static class CommandLine
{
    public static bool HasOption(params string[] words)
    {
        string[] forms = GetForms(words);
        return Environment.GetCommandLineArgs().Any(arg => Matches(arg, forms));
    }

    public static string GetOptionValue(params string[] words)
    {
        string[] forms = GetForms(words);
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            int assignment = arg.IndexOf('=');

            if (assignment >= 0)
            {
                if (Matches(arg.Substring(0, assignment), forms))
                    return arg.Substring(assignment + 1);

                continue;
            }

            if (Matches(arg, forms) && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    // The canonical spelling of an option, for log and error messages.
    public static string Format(params string[] words) => GetForms(words)[0];

    private static bool Matches(string arg, string[] forms) =>
        forms.Any(form => arg.Equals(form, StringComparison.OrdinalIgnoreCase));

    private static string[] GetForms(string[] words) =>
        [
            "--" + string.Join("-", words),
            "/" + ToPascalCase(words),
            "-" + words[0] + ToPascalCase(words.Skip(1)),
        ];

    private static string ToPascalCase(IEnumerable<string> words) =>
        string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
}
