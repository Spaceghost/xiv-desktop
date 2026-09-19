namespace XivDesktop.Core.Ask;

/// <summary>An equipment slot's model: the game's EquipmentModelId (id, variant, two dyes).</summary>
public readonly record struct EquipModel(ushort Id, byte Variant, byte Stain0 = 0, byte Stain1 = 0)
{
    /// <summary>ENpcBase/NpcEquip pack a slot as id | variant &lt;&lt; 16 in a uint.</summary>
    public static EquipModel FromSheet(uint packed, byte stain0 = 0, byte stain1 = 0) =>
        new((ushort)(packed & 0xFFFF), (byte)((packed >> 16) & 0xFF), stain0, stain1);
}

/// <summary>A weapon's model: the game's WeaponModelId (id, type, variant, two dyes).</summary>
public readonly record struct WeaponModel(ushort Id, ushort Type, ushort Variant, byte Stain0 = 0, byte Stain1 = 0)
{
    /// <summary>ENpcBase/NpcEquip pack a weapon as id | type &lt;&lt; 16 | variant &lt;&lt; 32 in a ulong.</summary>
    public static WeaponModel FromSheet(ulong packed, byte stain0 = 0, byte stain1 = 0) =>
        new((ushort)(packed & 0xFFFF), (ushort)((packed >> 16) & 0xFFFF), (ushort)((packed >> 32) & 0xFFFF), stain0, stain1);

    public bool IsEmpty => Id == 0;
}

/// <summary>
/// Everything needed to dress a spawned character: a ModelChara row (0 = a human body built from
/// <see cref="Customize"/>), the 26 customize bytes, the ten equipment slots in the game's order (head,
/// body, hands, legs, feet, ears, neck, wrists, right ring, left ring) and two weapons.
/// </summary>
public sealed record NpcLook(int ModelCharaId, byte[] Customize, EquipModel[] Equipment, WeaponModel MainHand, WeaponModel OffHand, float Scale = 1f)
{
    public const int CustomizeLength = 26;
    public const int EquipmentSlots = 10;

    public bool IsHuman => ModelCharaId == 0;

    /// <summary>A non-human model (moogle, minion, mount, pet) with no customize or gear.</summary>
    public static NpcLook Model(int modelCharaId, float scale = 1f) =>
        new(modelCharaId, new byte[CustomizeLength], new EquipModel[EquipmentSlots], default, default, scale);

    /// <summary>
    /// Builds a look from an ENpcBase row's values. <paramref name="customize"/> is the row's 26 customize
    /// columns in sheet order (Race, Gender, BodyType, Height, Tribe, Face, HairStyle, HairHighlight,
    /// SkinColor, EyeHeterochromia, HairColor, HairHighlightColor, FacialFeature, FacialFeatureColor,
    /// Eyebrows, EyeColor, EyeShape, Nose, Jaw, Mouth, LipColor, BustOrTone1, ExtraFeature1,
    /// ExtraFeature2OrBust, FacePaint, FacePaintColor), which is exactly the game's customize byte order.
    /// <paramref name="models"/> are the ten packed slot values in sheet order (Head, Body, Hands, Legs,
    /// Feet, Ears, Neck, Wrists, LeftRing, RightRing); the game's slot 8 is the right ring, so the rings
    /// swap. Stains are per slot in the same sheet order.
    /// </summary>
    public static NpcLook FromENpc(int modelCharaId, ReadOnlySpan<byte> customize, ReadOnlySpan<uint> models, ReadOnlySpan<byte> stains, ReadOnlySpan<byte> stains2,
        ulong mainHand, ulong offHand, byte mainStain = 0, byte offStain = 0, float scale = 1f)
    {
        var c = new byte[CustomizeLength];
        customize[..Math.Min(customize.Length, CustomizeLength)].CopyTo(c);
        var eq = new EquipModel[EquipmentSlots];
        for (var sheet = 0; sheet < Math.Min(models.Length, EquipmentSlots); sheet++)
        {
            var slot = sheet switch
            {
                8 => 9, // sheet LeftRing → game slot 9 (left ring)
                9 => 8, // sheet RightRing → game slot 8 (right ring)
                _ => sheet,
            };
            eq[slot] = EquipModel.FromSheet(models[sheet], sheet < stains.Length ? stains[sheet] : (byte)0, sheet < stains2.Length ? stains2[sheet] : (byte)0);
        }

        return new NpcLook(modelCharaId, c, eq, WeaponModel.FromSheet(mainHand, mainStain), WeaponModel.FromSheet(offHand, offStain), scale <= 0 ? 1f : scale);
    }
}
