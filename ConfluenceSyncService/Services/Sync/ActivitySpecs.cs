namespace ConfluenceSyncService.Services.Sync
{
    public sealed class ActivitySpec
    {
        public string Key { get; init; } = "";            // unique within workflow
        public string TaskCategory { get; init; } = "";
        public string TaskName { get; init; } = "";
        public string DefaultRole { get; init; } = "";
        public string AnchorDateType { get; init; } = ""; // "GoLive" | "HypercareEnd"
        public int StartOffsetDays { get; init; }
        public int DurationBusinessDays { get; init; }
    }

    public static class MvpActivityCatalog
    {
        // Fallback activities (used if template can’t be parsed)
        public static IReadOnlyList<ActivitySpec> ForSupportTransition() => new[]
        {
            new ActivitySpec {
                Key="prep-chase",
                TaskCategory="Support Transition Packet Delivered - T-4 weeks",
                TaskName="Gentle chaser - PM ensure preparedness",
                DefaultRole="Project PM",
                AnchorDateType="GoLive",
                StartOffsetDays=-20,
                DurationBusinessDays=4
            },
            new ActivitySpec {
                Key="hypercare-handover",
                TaskCategory="Hypercare ending — handover",
                TaskName="Confirm support handover readiness",
                DefaultRole="Support Resource",
                AnchorDateType="HypercareEnd",
                StartOffsetDays=-5,
                DurationBusinessDays=3
            },
            new ActivitySpec {
                Key="cutover-retro",
                TaskCategory="Post Go-Live",
                TaskName="Cutover retrospective logged",
                DefaultRole="Support Resource",
                AnchorDateType="GoLive",
                StartOffsetDays=5,
                DurationBusinessDays=1
            }
        };
    }
}
