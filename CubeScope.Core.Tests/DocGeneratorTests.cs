using System.Text.RegularExpressions;
using CubeScope.Core.Models;
using CubeScope.Core.Script;

namespace CubeScope.Core.Tests;

public class DocGeneratorTests
{
    [Fact]
    public void Generate_MatchesGoldenMarkdown()
    {
        var meta = new CubeMeta("Ventes",
            [
                new MeasureFolder("", [new MeasureMeta("CA", "[Measures].[CA]")]),
                new MeasureFolder("Coûts", [new MeasureMeta("Achats", "[Measures].[Achats]")]),
            ],
            [
                new DimensionMeta("Dates", "[Dates]", [
                    new HierarchyMeta("Calendrier", "[Dates].[Calendrier]", [
                        new LevelMeta("Année", "[Dates].[Calendrier].[Année]", 1),
                        new LevelMeta("Mois", "[Dates].[Calendrier].[Mois]", 2),
                    ]),
                ]),
            ]);
        var script = new CubeScript("Ventes", "",
        [
            new ScriptCommand("CalculatedMember", "[Measures].[Marge]", "[Measures].[CA] - [Measures].[Achats]", 3),
            new ScriptCommand("CalculatedMember", "[Measures].[Taux]", "[Measures].[Marge] / [Measures].[CA]", 7),
            new ScriptCommand("NamedSet", "[Années récentes]", "Tail([Dates].[Calendrier].[Année].Members, 2)", 10),
            new ScriptCommand("Scope", "SCOPE([Measures].[CA]);", "SCOPE([Measures].[CA]); THIS = 1; END SCOPE", 12),
        ]);

        string doc = DocGenerator.Generate(meta, script, "SRV", "Db").ReplaceLineEndings("\n");
        // The generation timestamp is the only non-deterministic part.
        doc = Regex.Replace(doc, @"le \d{4}-\d{2}-\d{2} \d{2}:\d{2}", "le <date>");

        const string expected = """
            # Cube [Ventes]

            > Généré par CubeScope le <date> — serveur `SRV`, catalogue `Db`.

            ## Dimensions (1)

            - **Dates** `[Dates]`
              - Calendrier : Année > Mois

            ## Mesures (2)

            ### (racine)

            - CA

            ### Coûts

            - Achats

            ## Membres calculés (2)

            ### [Measures].[Marge]

            ```mdx
            [Measures].[CA] - [Measures].[Achats]
            ```
            Dépend de : `[Measures].[Achats]`, `[Measures].[CA]`

            ### [Measures].[Taux]

            ```mdx
            [Measures].[Marge] / [Measures].[CA]
            ```
            Dépend de : `[Measures].[CA]`, `[Measures].[Marge]`

            ## Sets nommés (1)

            ### [Années récentes]

            ```mdx
            Tail([Dates].[Calendrier].[Année].Members, 2)
            ```

            ## Blocs SCOPE (1)

            - Ligne 12 : `SCOPE([Measures].[CA]);`


            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), doc);
    }
}
