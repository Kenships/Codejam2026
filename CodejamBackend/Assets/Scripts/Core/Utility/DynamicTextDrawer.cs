using UnityEngine;

namespace Core.Utility
{
    public class DynamicTextAreaAttribute : PropertyAttribute
    {
        public readonly float minLines;

        public DynamicTextAreaAttribute(float minLines = 2f)
        {
            this.minLines = minLines;
        }
    }
}
