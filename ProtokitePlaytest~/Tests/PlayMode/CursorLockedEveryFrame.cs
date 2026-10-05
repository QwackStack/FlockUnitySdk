using UnityEngine;

namespace Protokite.Playtest.Tests
{
    /// <summary>A game that locks its cursor again every frame, late, as many first-person controllers do.</summary>
    internal sealed class CursorLockedEveryFrame : MonoBehaviour
    {
        private void LateUpdate()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
