namespace Mmd.Modern.Rendering.D3D12;

/// <summary>按 PMX 材质名猜测角色材质类型，供没有手动标记的材质使用。
/// 只看名字，不看贴图或几何；猜不出的一律当作 Body。手动标记始终优先。</summary>
public static class D3D12CharacterMaterialRoleGuess
{
    // 先匹配先得，不区分大小写。眉放在眼前面，"eyebrow" 归脸而不是眼。
    // 不收“肌”：它也指身体皮肤，脸部皮肤只靠“顔／颜／顏／脸”等识别。
    private static readonly (D3D12CharacterMaterialRole Role, string[] Keywords)[] Keywords =
    [
        (D3D12CharacterMaterialRole.Face, ["眉", "brow"]),
        (D3D12CharacterMaterialRole.Eye, ["目", "眼", "eye", "瞳", "ハイライト", "白目", "睫", "まつ", "lash"]),
        (D3D12CharacterMaterialRole.Hair, ["髪", "发", "髮", "頭髪", "hair", "前髪", "後髪"]),
        (D3D12CharacterMaterialRole.Face, ["顔", "颜", "顏", "脸", "臉", "face", "口", "mouth", "歯", "齿", "齒", "舌",
            "頬", "cheek", "表情", "biaoq"]),
    ];

    public static D3D12CharacterMaterialRole Guess(string? materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return D3D12CharacterMaterialRole.Body;
        foreach (var (role, keywords) in Keywords)
            foreach (string keyword in keywords)
                if (materialName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return role;
        return D3D12CharacterMaterialRole.Body;
    }
}
