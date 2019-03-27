using System.Collections.Generic;

namespace SpssLib.SpssDataset
{
    public class NumericVariable : Variable
    {
        /// <summary>
        /// The labels for different values
        /// </summary>
        public IDictionary<double, string> ValueLabels { get; set; }

        public NumericVariable()
        {
            ValueLabels = new Dictionary<double, string>();
        }

        public NumericVariable(string name) : base(name)
        {
            
            ValueLabels = new Dictionary<double, string>();
        }
    }
    
    public class TextVariable : Variable
    {
        /// <summary>
        /// The labels for different values
        /// </summary>
        public IDictionary<string, string> ValueLabels { get; set; }

        public TextVariable()
        {
            ValueLabels = new Dictionary<string, string>();
        }

        public TextVariable(string name) : base(name)
        {
            
            ValueLabels = new Dictionary<string, string>();
        }
    }
}