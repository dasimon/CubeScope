using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class KeyListTests
{
    private const string Isin = "[Titre].[ISIN].[ISIN]";

    [Fact]
    public void Tous_les_separateurs_acceptes()
    {
        Assert.Equal(["A", "B", "C", "D", "E", "F"],
            KeyList.Parse("A\r\nB\nC,D;E\tF"));
    }

    [Fact]
    public void Espaces_supprimes_vides_et_doublons_retires_ordre_conserve()
    {
        Assert.Equal(["FR001", "LU002", "FR003"],
            KeyList.Parse("  FR001 \n\n LU002;;FR001,\t FR003 \r\n LU002 "));
    }

    [Fact]
    public void Doublons_sensibles_a_la_casse()
    {
        Assert.Equal(["ab", "AB"], KeyList.Parse("ab\nAB"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ; , \t ")]
    public void Texte_vide(string? text) => Assert.Empty(KeyList.Parse(text));

    [Fact]
    public void Guillemets_csv_et_espaces_invisibles_retires()
    {
        Assert.Equal(["FR001", "LU002", "a\"b"],
            KeyList.Parse($"\"FR001\"\n{(char)0x200B}LU002{(char)0xFEFF}\n\"a\"\"b\""));
    }

    [Fact]
    public void Espace_interne_conserve()
    {
        Assert.Equal(["Fonds A"], KeyList.Parse("  Fonds A  "));
    }

    [Fact]
    public void Nom_unique_sous_le_niveau()
    {
        Assert.Equal("[Titre].[ISIN].[ISIN].&[FR0000120271]", KeyList.MemberUniqueName(Isin, "FR0000120271"));
    }

    [Fact]
    public void Crochet_fermant_double_apostrophe_intacte()
    {
        Assert.Equal("[Titre].[ISIN].[ISIN].&[A]]B]", KeyList.MemberUniqueName(Isin, "A]B"));
        Assert.Equal("[Titre].[ISIN].[ISIN].&[L'Oréal]", KeyList.MemberUniqueName(Isin, "L'Oréal"));
    }

    [Fact]
    public void Cle_composite_passe_telle_quelle()
    {
        Assert.Equal("[Dim].[Hier].[Niv].&[2025]&[FR]", KeyList.MemberUniqueName("[Dim].[Hier].[Niv]", "&[2025]&[FR]"));
    }

    [Fact]
    public void Tentative_d_injection_reste_une_cle()
    {
        // Not a composite (closing bracket not doubled): wrapped and escaped, so it stays one key.
        Assert.Equal("[Titre].[ISIN].[ISIN].&[X]]} ON 0 FROM [C]",
            KeyList.MemberUniqueName(Isin, "X]} ON 0 FROM [C"));
        Assert.Equal("[Titre].[ISIN].[ISIN].&[&[a]].Children]",
            KeyList.MemberUniqueName(Isin, "&[a].Children"));
    }

    [Fact]
    public void Niveau_invalide_refuse()
    {
        Assert.Throws<MdxBuilderException>(() => KeyList.MemberUniqueName("Titre.ISIN", "FR001"));
    }
}
