namespace Domain.ValueObjects
{
    public readonly record struct TimeRange
    {
        public DateTime OpenTime { get; }
        public DateTime CloseTime { get; }

        public TimeRange(DateTime openTime, DateTime closeTime)
        {
            if (openTime == default || openTime == DateTime.MinValue)
                throw new ArgumentException("OpenTime cannot be default or min value.", nameof(openTime));

            if (closeTime == default || closeTime == DateTime.MinValue)
                throw new ArgumentException("CloseTime cannot be default or min value.", nameof(closeTime));

            if (closeTime < openTime)
                throw new ArgumentException("CloseTime cannot be earlier than OpenTime.", nameof(closeTime));

            OpenTime = openTime;
            CloseTime = closeTime;
        }
    }
}
