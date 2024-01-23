﻿using MHServerEmu.Games.GameData.Calligraphy;

namespace MHServerEmu.Games.GameData.Prototypes
{
    #region Enums

    [AssetEnum((int)Standard)]
    public enum AreaMinimapReveal
    {
        Standard,
        PlayerAreaOnly,
        PlayerCellOnly,
        PlayerAreaGroup,
    }

    #endregion

    public class AreaPrototype : Prototype
    {
        public GeneratorPrototype Generator { get; protected set; }
        public PrototypeId Population { get; protected set; }
        public LocaleStringId AreaName { get; protected set; }
        public PrototypeId PropDensity { get; protected set; }
        public StringId[] PropSets { get; protected set; }
        public StyleEntryPrototype[] Styles { get; protected set; }
        public StringId ClientMap { get; protected set; }
        public StringId[] Music { get; protected set; }
        public bool FullyGenerateCells { get; protected set; }
        public AreaMinimapReveal MinimapRevealMode { get; protected set; }
        public StringId AmbientSfx { get; protected set; }
        public LocaleStringId MinimapName { get; protected set; }
        public int MinimapRevealGroupId { get; protected set; }
        public PrototypeId RespawnOverride { get; protected set; }
        public PrototypeId PlayerCameraSettings { get; protected set; }
        public FootstepTraceBehaviorAsset FootstepTraceOverride { get; protected set; }
        public RegionMusicBehaviorAsset MusicBehavior { get; protected set; }
        public PrototypeId[] Keywords { get; protected set; }
        public int LevelOffset { get; protected set; }
        public RespawnCellOverridePrototype[] RespawnCellOverrides { get; protected set; }
        public PrototypeId PlayerCameraSettingsOrbis { get; protected set; }
    }

    public class AreaTransitionPrototype : Prototype
    {
        public StringId Type { get; protected set; }
    }

    public class RespawnCellOverridePrototype : Prototype
    {
        public StringId[] Cells { get; protected set; }
        public PrototypeId RespawnOverride { get; protected set; }
    }

    public class StyleEntryPrototype : Prototype
    {
        public PrototypeId Population { get; protected set; }
        public StringId[] PropSets { get; protected set; }
        public int Weight { get; protected set; }
    }

    public class AreaListPrototype : Prototype
    {
        public PrototypeId[] Areas { get; protected set; }
    }
}
