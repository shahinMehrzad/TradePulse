namespace Application.Dtos.Lookups
{
    public class TimeFramesResponseDto : BaseLookupDto
    {
        public int IntervalToSeconds { get; set; }
        public string FriendlyName
        {
            get
            {
                switch (IntervalToSeconds)
                {
                    case 1: return "1 Second";
                    case 60: return "1 Minute";
                    case 180: return "3 Minute";
                    case 300: return "5 Minutes";
                    case 900: return "15 Minutes";
                    case 1800: return "30 Minutes";
                    case 3600: return "1 Hour";
                    case 7200: return "2 Hour";
                    case 14400: return "4 Hours";
                    case 21600: return "6 Hours";
                    case 28800: return "8 Hours";
                    case 43200: return "12 Hours";
                    case 86400: return "1 Day";
                    default: return $"{IntervalToSeconds} Seconds";
                }
            }
        }
    }
}
