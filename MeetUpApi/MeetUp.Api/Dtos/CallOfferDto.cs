namespace MeetUp.Api.Dtos
{
    public class CallOfferDto
    {
        public string From { get; set; }
        public string To { get; set; }
        public string RoomId { get; set; }
        public SessionDescriptionDto Offer { get; set; }
        public string? FromUsername { get; set; }
        public string? ToUsername { get; set; }

        public CallOfferDto() {
            From = string.Empty;
            To = string.Empty;
            RoomId = string.Empty;
            Offer = new SessionDescriptionDto();
        }

        public override string ToString()
        {
            return $"{From} - {To} - {RoomId} - {Offer?.Type ?? "NULL"}";
        }


    }
}
