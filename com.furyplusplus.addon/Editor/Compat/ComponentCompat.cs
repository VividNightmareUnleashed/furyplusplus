using System;
using System.Collections.Generic;
using UnityEngine;

namespace FuryPlusPlus {
    /** Unity component metadata used while replaying a cached avatar hierarchy. */
    internal static class ComponentCompat {
        internal static IEnumerable<Type> RequiredComponentTypes(Component component) {
            foreach (RequireComponent require in component.GetType()
                         .GetCustomAttributes(typeof(RequireComponent), true)) {
                if (require.m_Type0 != null) yield return require.m_Type0;
                if (require.m_Type1 != null) yield return require.m_Type1;
                if (require.m_Type2 != null) yield return require.m_Type2;
            }
        }
    }
}
