namespace DofusRetroFuture.Core;

public static class Constants
{
	// Isometric grid
	public const float CellWidth = 53f;
	public const float CellHalfWidth = 26.5f;
	public const float CellHeight = 27f;
	public const float CellHalfHeight = 13.5f;
	public const float LevelHeight = 20f;

	// Display
	public const float DisplayWidth = 742f;
	public const float DisplayHeight = 532f;  // Map (432) + UI bar (100)
	public const float MapHeight = 432f;
	public const float UiBarHeight = DisplayHeight - MapHeight;

	// Direction mapping: game direction → { suffix, flip }
	// 0=E→S, 1=SE→R, 2=S→F, 3=SW→R(flip), 4=W→S(flip), 5=NW→L, 6=N→B, 7=NE→L(flip)
	public static readonly string[] DirSuffix = ["S", "R", "F", "R", "S", "L", "B", "L"];
	public static readonly bool[] DirFlip = [false, false, false, true, true, false, false, true];

	// Animation FPS (matching Pixi constants)
	public const int StaticFps = 12;
	public const int WalkFps = 24;
	public const int RunFps = 30;

	// Stress test
	public static readonly int[] GfxPool = [
		10, 11, 20, 21, 30, 31, 40, 41, 50, 51, 60, 61,
		70, 71, 80, 81, 90, 91, 100, 101, 110, 111, 120, 121
	];

	public static readonly string[] HatPool = [
		"16_10", "16_3", "16_4", "16_11", "16_12", "16_102", "16_103", "16_104",
		"16_105", "16_106", "16_107", "16_108", "16_109", "16_110", "16_111",
		"16_112", "16_113", "16_114", "16_115", "16_116", "16_117", "16_118",
		"16_119", "16_120", "16_121", "16_122", "16_123", "16_124", "16_125",
		"16_126", "16_127"
	];

	public static readonly string[] CapePool = [
		"17_5", "17_10", "17_17", "17_19", "17_21", "17_88", "17_1", "17_2",
		"17_3", "17_4", "17_6", "17_7", "17_8", "17_9", "17_11", "17_12",
		"17_13", "17_14", "17_15", "17_16"
	];

	public static readonly string[] ShieldPool = [
		"82_10", "82_30", "82_37", "82_39", "82_1", "82_2", "82_3", "82_4",
		"82_5", "82_6", "82_7", "82_8", "82_9", "82_11", "82_12", "82_13",
		"82_14", "82_15"
	];

	public static readonly string[] PlayerNames = [
		"Xelor-du-Temps", "Iop-le-Brave", "Cra-la-Fleche", "Sadida-Vert",
		"Eniripsa-Douce", "Ecaflip-Lucky", "Osamodas-Fou", "Sram-Ombre",
		"Feca-Shield", "Sacrieur-Blood", "Pandawa-Ivre", "Enutrof-Gold"
	];

	public static int GetAnimFps(string animName)
	{
		if (animName.StartsWith("static")) return StaticFps;
		if (animName.StartsWith("run")) return RunFps;
		return WalkFps;
	}
}
