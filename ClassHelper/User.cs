namespace NewSanofi.ClassHelper
{
    public class User
    {
        public string Name { get; set; }
        public string Password { get; set; }
        public string Type { get; set; }
        public static User UserInstance { get; set; }
    }
}
