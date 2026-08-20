using Newtonsoft.Json.Linq;

namespace NTE.DialogueStructureProbe;

internal static class JTokenTraversalExtensions
{
    public static IEnumerable<JToken> DescendantsAndSelf(this JToken token)
    {
        yield return token;

        if (token is not JContainer container)
            yield break;

        foreach (var descendant in container.Descendants())
            yield return descendant;
    }
}
