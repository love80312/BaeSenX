using System.Collections.Generic;

namespace BaeSenX
{
    public class Instruction
    {
        public string Type { get; set; } = string.Empty;
        public List<string> Arguments { get; set; }

        public Instruction()
        {
            Arguments = new List<string>();
        }
    }
}
