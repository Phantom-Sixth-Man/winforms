﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Drawing.Design;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Microsoft.Win32;

namespace System.Windows.Forms.Design
{
    public partial class DocumentDesigner
    {
        //
        // <doc>
        // <desc>
        //      Toolbox item we implement so we can create ActiveX controls.
        // </desc>
        // <internalonly/>
        // </doc>
        [Serializable]
        private class AxToolboxItem : ToolboxItem
        {
            private string clsid;
            private Type axctlType = null;
            private string version = String.Empty;

            public AxToolboxItem(string clsid) : base(typeof(AxHost))
            {
                this.clsid = clsid;
                this.Company = null; //we don't get any company info for ax controls.
                LoadVersionInfo();
            }

            // Since we don't call the base constructor here, which does call Deserialize which we
            // override, we should be okay.
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
            private AxToolboxItem(SerializationInfo info, StreamingContext context)
            {
                Deserialize(info, context);

            }

            /// <include file='doc\ToolboxItem.uex' path='docs/doc[@for="ToolboxItem.ComponentType"]/*' />
            /// <devdoc>
            ///     The Component Type is ".Net Component" -- unless otherwise specified by a derived toolboxitem
            /// </devdoc>
            public override string ComponentType
            {
                get
                {
                    return SR.Ax_Control;
                }
            }

            public override string Version
            {
                get
                {
                    return version;
                }
            }

            private void LoadVersionInfo()
            {
                string controlKey = "CLSID\\" + clsid;
                RegistryKey key = Registry.ClassesRoot.OpenSubKey(controlKey);

                //fail later -- not for tooltip info...
                if (key != null)
                {
                    RegistryKey verKey = key.OpenSubKey("Version");
                    if (verKey != null)
                    {
                        version = (string)verKey.GetValue("");
                        verKey.Close();
                    }
                    key.Close();
                }
            }

            /// <include file='doc\DocumentDesigner.uex' path='docs/doc[@for="DocumentDesigner.AxToolboxItem.CreateComponentsCore"]/*' />
            /// <devdoc>
            /// <para>Creates an instance of the ActiveX control. Calls VS7 project system
            /// to generate the wrappers if they are needed..</para>
            /// </devdoc>
            protected override IComponent[] CreateComponentsCore(IDesignerHost host)
            {
                IComponent[] comps = null;
                Debug.Assert(host != null, "Designer host is null!!!");

                // Get the DTE References object
                //
                object references = GetReferences(host);
                if (references != null)
                {
                    try
                    {
                        TYPELIBATTR tlibAttr = GetTypeLibAttr();

                        object[] args = new object[5];
                        args[0] = "{" + tlibAttr.guid.ToString() + "}";
                        args[1] = (int)tlibAttr.wMajorVerNum;
                        args[2] = (int)tlibAttr.wMinorVerNum;
                        args[3] = (int)tlibAttr.lcid;

                        args[4] = "";
                        object tlbRef = references.GetType().InvokeMember("AddActiveX", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, references, args, CultureInfo.InvariantCulture);
                        Debug.Assert(tlbRef != null, "Null reference returned by AddActiveX (tlbimp) by the project system for: " + clsid);

                        args[4] = "aximp";
                        object axRef = references.GetType().InvokeMember("AddActiveX", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, references, args, CultureInfo.InvariantCulture);
                        Debug.Assert(axRef != null, "Null reference returned by AddActiveX (aximp) by the project system for: " + clsid);

                        axctlType = GetAxTypeFromReference(axRef, host);

                    }
                    catch (TargetInvocationException tie)
                    {
                        Debug.WriteLineIf(DocumentDesigner.AxToolSwitch.TraceVerbose, "Generating Ax References failed: " + tie.InnerException);
                        throw tie.InnerException;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLineIf(DocumentDesigner.AxToolSwitch.TraceVerbose, "Generating Ax References failed: " + e);
                        throw e;
                    }
                }

                if (axctlType == null)
                {
                    IUIService uiSvc = (IUIService)host.GetService(typeof(IUIService));
                    if (uiSvc == null)
                    {
                        RTLAwareMessageBox.Show(null, SR.AxImportFailed, null, MessageBoxButtons.OK, MessageBoxIcon.Error,
                                        MessageBoxDefaultButton.Button1, 0);
                    }
                    else
                    {
                        uiSvc.ShowError(SR.AxImportFailed);
                    }
                    return new IComponent[0];
                }

                comps = new IComponent[1];
                try
                {
                    comps[0] = host.CreateComponent(axctlType);
                }
                catch (Exception e)
                {
                    Debug.Fail("Could not create type: " + e);
                    throw e;
                }

                Debug.Assert(comps[0] != null, "Could not create instance of ActiveX control wrappers!!!");
                return comps;
            }

            /// <include file='doc\DocumentDesigner.uex' path='docs/doc[@for="DocumentDesigner.AxToolboxItem.Deserialize"]/*' />
            /// <devdoc>
            /// <para>Loads the state of this 'AxToolboxItem'
            /// from the stream.</para>
            /// </devdoc>
            protected override void Deserialize(SerializationInfo info, StreamingContext context)
            {
                base.Deserialize(info, context);
                clsid = info.GetString("Clsid");
            }

            /// <devdoc>
            /// <para>Gets hold of the DTE Reference object and from there, opens the assembly of the
            /// ActiveX control we want to create. It then walks through all AxHost derived classes
            /// in that assembly, and returns the type that matches our control's CLSID.</para>
            /// </devdoc>
            private Type GetAxTypeFromReference(object reference, IDesignerHost host)
            {
                string path = (string)reference.GetType().InvokeMember("Path", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, reference, null, CultureInfo.InvariantCulture);

                // Missing reference will show up as an empty string.
                //
                if (path == null || path.Length <= 0)
                {
                    return null;
                }

                FileInfo file = new FileInfo(path);
                string fullPath = file.FullName;
                Debug.WriteLineIf(AxToolSwitch.TraceVerbose, "Checking: " + fullPath);

                ITypeResolutionService trs = (ITypeResolutionService)host.GetService(typeof(ITypeResolutionService));
                Debug.Assert(trs != null, "No type resolution service found.");

                Assembly a = trs.GetAssembly(AssemblyName.GetAssemblyName(fullPath));
                Debug.Assert(a != null, "No assembly found at " + fullPath);

                return GetAxTypeFromAssembly(a);
            }

            /// <devdoc>
            /// <para>Walks through all AxHost derived classes in the given assembly, 
            /// and returns the type that matches our control's CLSID.</para>
            /// </devdoc>
            private Type GetAxTypeFromAssembly(Assembly a)
            {
                Type[] types = a.GetTypes();
                int len = types.Length;
                for (int i = 0; i < len; ++i)
                {
                    Type t = types[i];
                    if (!(typeof(AxHost).IsAssignableFrom(t)))
                    {
                        continue;
                    }

                    object[] attrs = t.GetCustomAttributes(typeof(AxHost.ClsidAttribute), false);
                    Debug.Assert(attrs != null && attrs.Length == 1, "Invalid number of GuidAttributes found on: " + t.FullName);

                    AxHost.ClsidAttribute guid = (AxHost.ClsidAttribute)attrs[0];
                    if (String.Equals(guid.Value, clsid, StringComparison.OrdinalIgnoreCase))
                    {
                        return t;
                    }
                }

                return null;
            }

            /// <devdoc>
            /// <para>Gets the References collection object from the designer host. The steps are:
            ///     Get the ProjectItem from the IDesignerHost.
            ///     Get the Containing Project of the ProjectItem.
            ///     Get the VSProject of the Containing Project.
            ///     Get the References property of the VSProject.</para>
            /// </devdoc>
            private object GetReferences(IDesignerHost host)
            {
                Debug.Assert(host != null, "Null Designer Host");

                Type type;
                object ext = null;
                type = Type.GetType("EnvDTE.ProjectItem, " + AssemblyRef.EnvDTE);

                if (type == null)
                {
                    return null;
                }

                ext = host.GetService(type);
                if (ext == null)
                    return null;

                string name = ext.GetType().InvokeMember("Name", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, ext, null, CultureInfo.InvariantCulture).ToString();

                object project = ext.GetType().InvokeMember("ContainingProject", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, ext, null, CultureInfo.InvariantCulture);
                Debug.Assert(project != null, "No DTE Project for the current project item: " + name);

                object vsproject = project.GetType().InvokeMember("Object", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, project, null, CultureInfo.InvariantCulture);
                Debug.Assert(vsproject != null, "No VS Project for the current project item: " + name);

                object references = vsproject.GetType().InvokeMember("References", BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, vsproject, null, CultureInfo.InvariantCulture);
                Debug.Assert(references != null, "No References for the current project item: " + name);

                return references;
            }

            /// <devdoc>
            /// <para>Gets the TypeLibAttr corresponding to the TLB containing our ActiveX control.</para>
            /// </devdoc>
            private TYPELIBATTR GetTypeLibAttr()
            {
                string controlKey = "CLSID\\" + clsid;
                RegistryKey key = Registry.ClassesRoot.OpenSubKey(controlKey);
                if (key == null)
                {
                    if (DocumentDesigner.AxToolSwitch.TraceVerbose)
                        Debug.WriteLine("No registry key found for: " + controlKey);
                    throw new ArgumentException(string.Format(SR.AXNotRegistered, controlKey.ToString()));
                }

                // Load the typelib into memory.
                //
                ITypeLib pTLB = null;

                // Try to get the TypeLib's Guid.
                //
                Guid tlbGuid = Guid.Empty;

                // Open the key for the TypeLib
                //
                RegistryKey tlbKey = key.OpenSubKey("TypeLib");

                if (tlbKey != null)
                {
                    // Get the major and minor version numbers.
                    //
                    RegistryKey verKey = key.OpenSubKey("Version");
                    Debug.Assert(verKey != null, "No version registry key found for: " + controlKey);

                    short majorVer = -1;
                    short minorVer = -1;
                    string ver = (string)verKey.GetValue("");
                    int dot = ver.IndexOf('.');
                    if (dot == -1)
                    {
                        majorVer = Int16.Parse(ver, CultureInfo.InvariantCulture);
                        minorVer = 0;
                    }
                    else
                    {
                        majorVer = Int16.Parse(ver.Substring(0, dot), CultureInfo.InvariantCulture);
                        minorVer = Int16.Parse(ver.Substring(dot + 1, ver.Length - dot - 1), CultureInfo.InvariantCulture);
                    }
                    Debug.Assert(majorVer > 0 && minorVer >= 0, "No Major version number found for: " + controlKey);
                    verKey.Close();

                    object o = tlbKey.GetValue("");
                    tlbGuid = new Guid((string)o);
                    Debug.Assert(!tlbGuid.Equals(Guid.Empty), "No valid Guid found for: " + controlKey);
                    tlbKey.Close();

                    try
                    {
                        pTLB = NativeMethods.LoadRegTypeLib(ref tlbGuid, majorVer, minorVer, Application.CurrentCulture.LCID);
                    }
                    catch (Exception e)
                    {
                        if (AxWrapperGen.AxWrapper.Enabled)
                            Debug.WriteLine("Failed to LoadRegTypeLib: " + e.ToString());
                        if (ClientUtils.IsCriticalException(e))
                        {
                            throw;
                        }
                    }

                }

                // Try to load the TLB directly from the InprocServer32.
                //
                // If that fails, try to load the TLB based on the TypeLib guid key.
                //
                if (pTLB == null)
                {
                    RegistryKey inprocServerKey = key.OpenSubKey("InprocServer32");
                    if (inprocServerKey != null)
                    {
                        string inprocServer = (string)inprocServerKey.GetValue("");
                        Debug.Assert(inprocServer != null, "No valid InprocServer32 found for: " + controlKey);
                        inprocServerKey.Close();

                        pTLB = NativeMethods.LoadTypeLib(inprocServer);
                    }
                }

                key.Close();

                if (pTLB != null)
                {
                    try
                    {
                        IntPtr pTlibAttr = NativeMethods.InvalidIntPtr;
                        pTLB.GetLibAttr(out pTlibAttr);
                        if (pTlibAttr == NativeMethods.InvalidIntPtr)
                            throw new ArgumentException(string.Format(SR.AXNotRegistered, controlKey.ToString()));
                        else
                        {
                            // Marshal the returned int as a TLibAttr structure
                            //
                            TYPELIBATTR typeLibraryAttributes = (TYPELIBATTR)Marshal.PtrToStructure(pTlibAttr, typeof(TYPELIBATTR));
                            pTLB.ReleaseTLibAttr(pTlibAttr);

                            return typeLibraryAttributes;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(pTLB);
                    }
                }
                else
                {
                    throw new ArgumentException(string.Format(SR.AXNotRegistered, controlKey.ToString()));
                }

            }

            /// <include file='doc\DocumentDesigner.uex' path='docs/doc[@for="DocumentDesigner.AxToolboxItem.Serialize"]/*' />
            /// <devdoc>
            /// <para>Saves the state of this 'AxToolboxItem' to
            ///    the specified serialization info.</para>
            /// </devdoc>
            protected override void Serialize(SerializationInfo info, StreamingContext context)
            {
                if (DocumentDesigner.AxToolSwitch.TraceVerbose)
                    Debug.WriteLine("Serializing AxToolboxItem:" + clsid);
                base.Serialize(info, context);
                info.AddValue("Clsid", clsid);
            }
        }
    }
}
