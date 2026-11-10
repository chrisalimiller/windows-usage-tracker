using System.Runtime.InteropServices;

namespace UsageTracker.Native;

// A minimal hand-written binding of the UI Automation client (UIAutomationCore.dll), used to
// read where the taskbar's own buttons are. Only the vtable slots up to the methods we call
// are declared; the slot order follows UIAutomationClient.idl. Unused slots are placeholders
// and must never be called.

[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation
{
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void get_ControlViewWalker();
    void get_ContentViewWalker();
    void get_RawViewWalker();
    void get_RawViewCondition();
    void get_ControlViewCondition();
    void get_ContentViewCondition();
    void CreateCacheRequest();
    void CreateTrueCondition();
    void CreateFalseCondition();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();

    [return: MarshalAs(UnmanagedType.Struct)]
    object GetCurrentPropertyValue(int propertyId);
}

[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    int get_Length();
    IUIAutomationElement GetElement(int index);
}

internal static class Uia
{
    public const int TreeScopeDescendants = 0x04;
    public const int BoundingRectanglePropertyId = 30001;
    public const int ControlTypePropertyId = 30003;
    public const int ClassNamePropertyId = 30012;
    public const int ButtonControlTypeId = 50000;
}
