namespace Mmd.Modern.Effects;

/// <summary>One command of an MME technique <c>Script</c>: a tiny declarative render graph.</summary>
public sealed record ScriptCommand(string Name, string Argument, string? Mode, int Index);

/// <summary>
/// Parses the <c>Script</c> annotation into commands.
/// </summary>
/// <remarks>
/// Real scripts are concatenated string literals such as
/// <c>"RenderColorTarget0=ScnMap;" "Clear=Color;" "ScriptExternal=Color;" "Pass=Gaussian_X;"</c>,
/// so the annotation must be parsed before it is split. Arguments are effect parameter names,
/// never literals — <c>RenderColorTarget0=ScnMap</c> resolves to the <c>texture ScnMap;</c>
/// declaration, which is how MME allocates the render target.
/// </remarks>
public static class EffectScript
{
    public static IReadOnlyList<ScriptCommand> Parse(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return [];
        }

        var commands = new List<ScriptCommand>();
        var current = new System.Text.StringBuilder();

        void Flush()
        {
            string statement = current.ToString().Trim();
            current.Clear();
            if (statement.Length == 0)
            {
                return;
            }

            int eq = statement.IndexOf('=');
            if (eq <= 0)
            {
                return;
            }

            string name = statement[..eq].Trim();
            string argument = statement[(eq + 1)..].Trim();

            string? mode = null;
            int open = argument.IndexOf('(');
            if (open > 0 && argument.EndsWith(')'))
            {
                mode = argument[(open + 1)..^1].Trim();
                argument = argument[..open].Trim();
            }

            if (name.Length > 0)
            {
                commands.Add(new ScriptCommand(name, argument, mode, commands.Count));
            }
        }

        int depth = 0;
        foreach (char c in script)
        {
            switch (c)
            {
                case '(':
                    depth++;
                    current.Append(c);
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    current.Append(c);
                    break;
                case ';' when depth == 0:
                    Flush();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush();
        return commands;
    }
}
