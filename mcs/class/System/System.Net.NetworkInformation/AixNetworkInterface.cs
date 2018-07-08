//
// System.Net.NetworkInformation.NetworkInterface
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@novell.com)
//	Atsushi Enomoto (atsushi@ximian.com)
//      Miguel de Icaza (miguel@novell.com)
//      Eric Butler (eric@extremeboredom.net)
//      Marek Habersack (mhabersack@novell.com)
//  Marek Safar (marek.safar@gmail.com)
//
// Copyright (c) 2006-2008 Novell, Inc. (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Net.NetworkInformation {
	internal class AixNetworkInterfaceAPI : UnixNetworkInterfaceAPI
	{
		// Address families that matter to us
		const int AF_INET  = 2;
		const int AF_INET6 = 30;
		const int AF_LINK  = 18;

		const int SOCK_DGRAM = 2;

		// ioctl commands that matter to us
		const uint SIOCGIFCONF    = 0xc0106945; /* list network interfaces */
		const uint SIOCGIFFLAGS   = 0xc0286911; /* get interface flags */
		const uint SIOCGIFNETMASK = 0xc0286925; /* get netmask for iface */
		const uint SIOCGIFMTU     = 0xc0286956; /* get mtu for iface */

		// AIX doesn't have getifaddrs, (i does though) so we instead query the painful way via ioctl. For IBM's docs on this, see:
		// https://www.ibm.com/support/knowledgecenter/en/ssw_aix_71/com.ibm.aix.commtrf2/ioctl_socket_control_operations.htm
		[DllImport("libc", SetLastError = true)]
		public static extern int socket (int family, int type, int protocol);
		[DllImport("libc")]
		public static extern int close (int fd);
		// overloads to make usage less painful
		[DllImport("libc", SetLastError = true)]
		public static extern int ioctl (int fd, uint request, IntPtr arg);
		[DllImport("libc", SetLastError = true)]
		public static extern int ioctl (int fd, uint request, ref AixStructs.ifconf arg);
		[DllImport("libc", SetLastError = true)]
		public static extern int ioctl (int fd, uint request, ref AixStructs.ifreq_flags arg);
		[DllImport("libc", SetLastError = true)]
		public static extern int ioctl (int fd, uint request, ref AixStructs.ifreq_mtu arg);
		[DllImport("libc", SetLastError = true)]
		public static extern int ioctl (int fd, uint request, ref AixStructs.ifreq_addrin arg);

		static unsafe void ByteArrayCopy (byte* dst, byte* src, int elements)
		{
			for (int i = 0; i < 16; i++)
				dst[i] = src[i];
		}

		public override NetworkInterface [] GetAllNetworkInterfaces ()
		{
			var interfaces = new Dictionary <string, AixNetworkInterface> ();
			AixStructs.ifconf ifc;
			// XXX: use SIOCGSIZIFCONF ioctl instead?
			ifc.ifc_len = 1024;
			ifc.ifc_buf = Marshal.AllocHGlobal(1024);
			int sockfd = -1;

			try {
				sockfd = socket (AF_INET, SOCK_DGRAM, 0);
				if (sockfd == -1)
					throw new SystemException ("socket for SIOCGIFCONF failed");

				if (ioctl (sockfd, SIOCGIFCONF, ref ifc) < 0)
					throw new SystemException ("ioctl for SIOCGIFCONF failed");

				// this is required because the buffer is an array of VARIABLE LENGTH structures, so sane marshalling is impossible
				AixStructs.ifreq ifr;
				var curPos = ifc.ifc_buf;
				var endPos = ifc.ifc_buf.ToInt64() + ifc.ifc_len;
				for (ifr = (AixStructs.ifreq)Marshal.PtrToStructure (curPos, typeof (AixStructs.ifreq));
				    curPos.ToInt64() < endPos;
				    // name length + sockaddr length (SIOCGIFCONF only deals in those)
				    curPos = curPos + (16 + ifr.ifru_addr.sa_len))
				{
					// update the structure for next increment
					ifr = (AixStructs.ifreq)Marshal.PtrToStructure (curPos, typeof (AixStructs.ifreq));

					// the goods
					IPAddress address = IPAddress.None;
					string    name = null;
					int       index = -1;
					byte[]    macAddress = null;
					var type = NetworkInterfaceType.Unknown;

					unsafe {
						name = Marshal.PtrToStringAnsi(new IntPtr(ifr.ifr_name));
					}

					switch (ifr.ifru_addr.sa_family) {
						case AF_INET:
							AixStructs.sockaddr_in sockaddrin =
								(AixStructs.sockaddr_in)Marshal.PtrToStructure(curPos + 16, typeof (AixStructs.sockaddr_in));
							address = new IPAddress (sockaddrin.sin_addr);
							break;
						case AF_INET6:
							AixStructs.sockaddr_in6 sockaddr6 =
								(AixStructs.sockaddr_in6) Marshal.PtrToStructure(curPos + 16, typeof (AixStructs.sockaddr_in6));
							address = new IPAddress (sockaddr6.sin6_addr.u6_addr8, sockaddr6.sin6_scope_id);
							break;
						// XXX: i never returns AF_LINK and SIOCGIFCONF under i doesn't return nameindex values; adapt MacOsNetworkInterface for Qp2getifaddrs instead
						case AF_LINK:
							AixStructs.sockaddr_dl sockaddrdl = new AixStructs.sockaddr_dl();
							sockaddrdl.Read (curPos + 16);

							macAddress = new byte [(int) sockaddrdl.sdl_alen];
							// copy mac address from sdl_data field starting at last index pos of interface name into array macaddress, starting
							// at index 0
							Array.Copy (sockaddrdl.sdl_data, sockaddrdl.sdl_nlen, macAddress, 0, Math.Min (macAddress.Length, sockaddrdl.sdl_data.Length - sockaddrdl.sdl_nlen));

							index = sockaddrdl.sdl_index;

							int hwtype = (int) sockaddrdl.sdl_type;
							if (Enum.IsDefined (typeof (AixArpHardware), hwtype)) {
								switch ((AixArpHardware) hwtype) {
									case AixArpHardware.ETHER:
										type = NetworkInterfaceType.Ethernet;
										break;

									case AixArpHardware.ATM:
										type = NetworkInterfaceType.Atm;
										break;

									case AixArpHardware.SLIP:
										type = NetworkInterfaceType.Slip;
										break;

									case AixArpHardware.PPP:
										type = NetworkInterfaceType.Ppp;
										break;

									case AixArpHardware.LOOPBACK:
										type = NetworkInterfaceType.Loopback;
										macAddress = null;
										break;

									case AixArpHardware.FDDI:
										type = NetworkInterfaceType.Fddi;
										break;
								}
							}
							break;
						default: break;
					}

					// get flags
					uint flags = 0;
					int mtu = 0;
					unsafe {
						AixStructs.ifreq_flags ifrFlags = new AixStructs.ifreq_flags ();
						ByteArrayCopy (ifrFlags.ifr_name, ifr.ifr_name, 16);
						if (ioctl (sockfd, SIOCGIFFLAGS, ref ifrFlags) < 0)
							throw new SystemException("ioctl for SIOCGIFFLAGS failed");
						else
							flags = ifrFlags.ifru_flags;

						AixStructs.ifreq_mtu ifrMtu = new AixStructs.ifreq_mtu ();
						ByteArrayCopy (ifrMtu.ifr_name, ifr.ifr_name, 16);
						if (ioctl (sockfd, SIOCGIFMTU, ref ifrMtu) < 0) {
							// it's not the end of the world if we don't get it
						}
						else
							mtu = ifrMtu.ifru_mtu;
					}

					AixNetworkInterface iface = null;

					// create interface if not already present
					if (!interfaces.TryGetValue (name, out iface)) {
						iface = new AixNetworkInterface (name, flags, mtu);
						interfaces.Add (name, iface);
					}

					// if a new address has been found, add it
					if (!address.Equals (IPAddress.None))
						iface.AddAddress (address);

					// set link layer info, if iface has macaddress or is loopback device
					if (macAddress != null || type == NetworkInterfaceType.Loopback)
						iface.SetLinkLayerInfo (index, macAddress, type);
				}
			} finally {
				Marshal.FreeHGlobal(ifc.ifc_buf);
				if (sockfd != -1)
					close (sockfd);
			}

			NetworkInterface [] result = new NetworkInterface [interfaces.Count];
			int x = 0;
			foreach (NetworkInterface thisInterface in interfaces.Values) {
				result [x] = thisInterface;
				x++;
			}
			return result;
		}

		public override int GetLoopbackInterfaceIndex ()
		{
			// XXX: "*LOOPBACK" on i
			return if_nametoindex ("lo0");
		}

		public override IPAddress GetNetMask (IPAddress address)
		{
			AixStructs.ifconf ifc;
			// XXX: use SIOCGSIZIFCONF ioctl instead?
			ifc.ifc_len = 1024;
			ifc.ifc_buf = Marshal.AllocHGlobal(1024);
			int sockfd = -1;

			try {
				sockfd = socket (AF_INET, SOCK_DGRAM, 0);
				if (sockfd == -1)
					throw new SystemException ("socket for SIOCGIFCONF failed");

				if (ioctl (sockfd, SIOCGIFCONF, ref ifc) < 0)
					throw new SystemException ("ioctl for SIOCGIFCONF failed");

				// this is required because the buffer is an array of VARIABLE LENGTH structures, so sane marshalling is impossible
				AixStructs.ifreq ifr;
				var curPos = ifc.ifc_buf;
				var endPos = ifc.ifc_buf.ToInt64() + ifc.ifc_len;
				for (ifr = (AixStructs.ifreq)Marshal.PtrToStructure (curPos, typeof (AixStructs.ifreq));
				    curPos.ToInt64() < endPos;
				    // name length + sockaddr length (SIOCGIFCONF only deals in those)
				    curPos += (16 + ifr.ifru_addr.sa_len))
				{
					// update the structure for next increment
					ifr = (AixStructs.ifreq)Marshal.PtrToStructure (curPos, typeof (AixStructs.ifreq));

					switch (ifr.ifru_addr.sa_family) {
						case AF_INET:
							AixStructs.sockaddr_in sockaddrin =
								(AixStructs.sockaddr_in)Marshal.PtrToStructure(curPos + 16, typeof (AixStructs.sockaddr_in));
							var saddress = new IPAddress (sockaddrin.sin_addr);
							if (address.Equals (saddress)) {
								AixStructs.ifreq_addrin ifrMask = new AixStructs.ifreq_addrin ();
								unsafe {
									ByteArrayCopy (ifrMask.ifr_name, ifr.ifr_name, 16);
								}
								// there's an IPv6 version of it too, but Mac OS doesn't try this, so
								if (ioctl (sockfd, SIOCGIFNETMASK, ref ifrMask) < 0)
									return new IPAddress(ifrMask.ifru_addr.sin_addr);
								else
									throw new SystemException("ioctl for SIOCGIFNETMASK failed");
							}
							break;
						default: break;
					}
				}
			} finally {
				Marshal.FreeHGlobal(ifc.ifc_buf);
				if (sockfd != -1)
					close (sockfd);
			}

			return null;
		}
	}

	sealed class AixNetworkInterface : UnixNetworkInterface
	{
		private uint _ifa_flags;
		private int _ifru_mtu;

		internal AixNetworkInterface (string name, uint ifa_flags, int ifru_mtu)
			: base (name)
		{
			_ifa_flags = ifa_flags;
			_ifru_mtu = ifru_mtu;
		}

		public override IPInterfaceProperties GetIPProperties ()
		{
			if (ipproperties == null)
				ipproperties = new AixIPInterfaceProperties (this, addresses, _ifru_mtu);
			return ipproperties;
		}

		public override IPv4InterfaceStatistics GetIPv4Statistics ()
		{
			if (ipv4stats == null)
				ipv4stats = new AixIPv4InterfaceStatistics (this);
			return ipv4stats;
		}

		public override OperationalStatus OperationalStatus {
			get {
				if(((AixInterfaceFlags)_ifa_flags & AixInterfaceFlags.IFF_UP) == AixInterfaceFlags.IFF_UP){
					return OperationalStatus.Up;
				}
				return OperationalStatus.Unknown;
			}
		}

		public override bool SupportsMulticast {
			get {
				return ((AixInterfaceFlags)_ifa_flags & AixInterfaceFlags.IFF_MULTICAST) == AixInterfaceFlags.IFF_MULTICAST;
			}
		}
	}
}

