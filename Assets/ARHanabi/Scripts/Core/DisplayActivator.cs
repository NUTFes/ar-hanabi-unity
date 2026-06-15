using UnityEngine;

public class DisplayActivator : MonoBehaviour
{
    private void Start()
    {
        if (Display.displays.Length > 1)
            Display.displays[1].Activate();
    }
}