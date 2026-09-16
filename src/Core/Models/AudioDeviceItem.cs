namespace CarroDesk.Core.Models
{
    public class AudioDeviceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public override string ToString() => Name;
    }
}