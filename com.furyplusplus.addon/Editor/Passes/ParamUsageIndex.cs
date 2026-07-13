using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace FuryPlusPlus {
    /**
     * Public-API analysis of which expression parameters the finished avatar actually
     * READS (animator conditions, blendtree parameters, state motion-time/speed/mirror/
     * cycle slots, and VRCAvatarParameterDriver Copy SOURCES — the read VRCFury's own
     * unused-param detection misses) versus merely writes (drivers, menu controls).
     * Dynamics-driven names (PhysBone/Contact parameter outputs) are collected separately.
     */
    internal sealed class ParamUsageIndex {
        internal readonly HashSet<string> Reads = new HashSet<string>();
        internal readonly HashSet<string> DriverWrites = new HashSet<string>();
        internal readonly HashSet<string> MenuWrites = new HashSet<string>();
        internal readonly HashSet<string> DynamicsParams = new HashSet<string>();

        private readonly HashSet<Motion> visitedMotions = new HashSet<Motion>();

        internal static ParamUsageIndex Build(VRCAvatarDescriptor descriptor) {
            var index = new ParamUsageIndex();
            foreach (var layer in AllPlayableLayers(descriptor)) {
                if (layer is AnimatorController controller) {
                    foreach (var controllerLayer in controller.layers) {
                        if (controllerLayer.stateMachine != null) {
                            index.WalkStateMachine(controllerLayer.stateMachine);
                        }
                    }
                }
            }
            index.WalkMenu(descriptor.expressionsMenu, new HashSet<VRCExpressionsMenu>());
            index.CollectDynamics(descriptor.gameObject);
            return index;
        }

        private static IEnumerable<RuntimeAnimatorController> AllPlayableLayers(VRCAvatarDescriptor descriptor) {
            if (descriptor.baseAnimationLayers != null) {
                foreach (var layer in descriptor.baseAnimationLayers) {
                    if (!layer.isDefault && layer.animatorController != null) yield return layer.animatorController;
                }
            }
            if (descriptor.specialAnimationLayers != null) {
                foreach (var layer in descriptor.specialAnimationLayers) {
                    if (!layer.isDefault && layer.animatorController != null) yield return layer.animatorController;
                }
            }
        }

        private void WalkStateMachine(AnimatorStateMachine machine) {
            foreach (var transition in machine.anyStateTransitions) AddConditions(transition.conditions);
            foreach (var transition in machine.entryTransitions) AddConditions(transition.conditions);
            foreach (var behaviour in machine.behaviours) AddBehaviour(behaviour);

            foreach (var child in machine.states) {
                var state = child.state;
                if (state == null) continue;
                foreach (var transition in state.transitions) AddConditions(transition.conditions);
                foreach (var behaviour in state.behaviours) AddBehaviour(behaviour);
                if (state.timeParameterActive) AddRead(state.timeParameter);
                if (state.speedParameterActive) AddRead(state.speedParameter);
                if (state.mirrorParameterActive) AddRead(state.mirrorParameter);
                if (state.cycleOffsetParameterActive) AddRead(state.cycleOffsetParameter);
                WalkMotion(state.motion);
            }

            foreach (var child in machine.stateMachines) {
                if (child.stateMachine == null) continue;
                foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine)) {
                    AddConditions(transition.conditions);
                }
                WalkStateMachine(child.stateMachine);
            }
        }

        private void WalkMotion(Motion motion) {
            if (!(motion is BlendTree tree) || !visitedMotions.Add(motion)) return;

            if (tree.blendType == BlendTreeType.Direct) {
                foreach (var child in tree.children) {
                    AddRead(child.directBlendParameter);
                    WalkMotion(child.motion);
                }
            } else {
                AddRead(tree.blendParameter);
                if (tree.blendType != BlendTreeType.Simple1D) AddRead(tree.blendParameterY);
                foreach (var child in tree.children) {
                    WalkMotion(child.motion);
                }
            }
        }

        private void AddBehaviour(StateMachineBehaviour behaviour) {
            if (!(behaviour is VRC_AvatarParameterDriver driver) || driver.parameters == null) return;
            foreach (var parameter in driver.parameters) {
                if (parameter == null) continue;
                if (parameter.type == VRC_AvatarParameterDriver.ChangeType.Copy) {
                    AddRead(parameter.source);
                }
                if (!string.IsNullOrEmpty(parameter.name)) DriverWrites.Add(parameter.name);
            }
        }

        private void AddConditions(IEnumerable<AnimatorCondition> conditions) {
            if (conditions == null) return;
            foreach (var condition in conditions) AddRead(condition.parameter);
        }

        private void AddRead(string name) {
            if (!string.IsNullOrEmpty(name)) Reads.Add(name);
        }

        private void WalkMenu(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> visited) {
            if (menu == null || !visited.Add(menu) || menu.controls == null) return;
            foreach (var control in menu.controls) {
                if (control == null) continue;
                if (!string.IsNullOrEmpty(control.parameter?.name)) MenuWrites.Add(control.parameter.name);
                if (control.subParameters != null) {
                    foreach (var sub in control.subParameters) {
                        if (!string.IsNullOrEmpty(sub?.name)) MenuWrites.Add(sub.name);
                    }
                }
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu) {
                    WalkMenu(control.subMenu, visited);
                }
            }
        }

        // PhysBones/Contacts live in precompiled dynamics DLLs; reach them by reflection so
        // the asmdef needs no extra precompiled references.
        private static readonly string[] PhysBoneSuffixes =
            { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        private void CollectDynamics(GameObject avatarRoot) {
            var physBoneType = ReflectionUtils.FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            var contactType = ReflectionUtils.FindType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver");

            foreach (var component in avatarRoot.GetComponentsInChildren<Component>(true)) {
                if (component == null) continue;
                var type = component.GetType();
                if (physBoneType != null && physBoneType.IsAssignableFrom(type)) {
                    var parameter = physBoneType.GetField("parameter")?.GetValue(component) as string;
                    if (!string.IsNullOrEmpty(parameter)) {
                        foreach (var suffix in PhysBoneSuffixes) DynamicsParams.Add(parameter + suffix);
                    }
                } else if (contactType != null && contactType.IsAssignableFrom(type)) {
                    var parameter = contactType.GetProperty("parameter")?.GetValue(component) as string
                                    ?? contactType.GetField("parameter")?.GetValue(component) as string;
                    if (!string.IsNullOrEmpty(parameter)) DynamicsParams.Add(parameter);
                }
            }
        }
    }
}
