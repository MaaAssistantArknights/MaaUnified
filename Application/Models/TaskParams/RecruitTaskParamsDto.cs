namespace MAAUnified.Application.Models.TaskParams;

public sealed class RecruitTaskParamsDto
{
    public int Times { get; set; } = 4;

    public bool Refresh { get; set; } = true;

    public bool ForceRefresh { get; set; } = true;

    public bool? UseExpedited { get; set; } = false;

    // Legacy v6.3.7 alias of PreserveTagEnabled (skip_robot). New configs store
    // the preserve-tag fields; reads keep accepting skip_robot and mirror it here.
    public bool SkipRobot { get; set; }

    // WPF dev-v2 RecruitTask.PreserveTagEnabled / PreserveTagList.
    public bool PreserveTagEnabled { get; set; }

    public List<string> PreserveTags { get; set; } = ["支援机械"];

    // WPF dev-v2 RecruitTask.Level3RecruitmentPermitReserveEnabled /
    // Level3RecruitmentPermitReserve. 0 is serialized when the toggle is off.
    public bool Level3RecruitmentPermitReserveEnabled { get; set; }

    public int Level3RecruitmentPermitReserve { get; set; } = 8;

    public int ExtraTagsMode { get; set; }

    public List<string> FirstTags { get; set; } = [];

    public bool ChooseLevel3 { get; set; } = true;

    public bool ChooseLevel4 { get; set; } = true;

    public bool ChooseLevel5 { get; set; }

    public bool ChooseLevel6 { get; set; }

    public int Level3Time { get; set; } = 540;

    public int Level4Time { get; set; } = 540;

    public int Level5Time { get; set; } = 540;

    public bool SetTime { get; set; } = true;
}
