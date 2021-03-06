// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using Rock;
using Rock.Attribute;
using Rock.CheckIn;
using Rock.Constants;
using Rock.Data;
using Rock.Model;
using Rock.Web.Cache;
using Rock.Web.UI.Controls;

namespace RockWeb.Blocks.CheckIn
{
    [DisplayName( "Administration" )]
    [Category( "Check-in" )]
    [Description( "Check-in Administration block" )]
    [BooleanField( "Allow Manual Setup", "If enabled, the block will allow the kiosk to be setup manually if it was not set via other means.", true, "", 5 )]
    [BooleanField( "Enable Location Sharing", "If enabled, the block will attempt to determine the kiosk's location via location sharing geocode.", false, "Geo Location", 6 )]
    [IntegerField( "Time to Cache Kiosk GeoLocation", "Time in minutes to cache the coordinates of the kiosk. A value of zero (0) means cache forever. Default 20 minutes.", false, 20, "Geo Location", 7 )]
    [BooleanField( "Enable Kiosk Match By Name", "Enable a kiosk match by computer name by doing reverseIP lookup to get computer name based on IP address", false, "", 8, "EnableReverseLookup" )]
    public partial class Admin : CheckInBlock
    {
        protected override void OnLoad( EventArgs e )
        {
            RockPage.AddScriptLink( "~/Blocks/CheckIn/Scripts/geo-min.js" );
            RockPage.AddScriptLink( "~/Scripts/iscroll.js" );
            RockPage.AddScriptLink( "~/Scripts/CheckinClient/checkin-core.js" );

            if ( !Page.IsPostBack )
            {
                // Set the check-in state from values passed on query string
                bool themeRedirect = PageParameter( "ThemeRedirect" ).AsBoolean( false );

                CurrentKioskId = PageParameter( "KioskId" ).AsIntegerOrNull();
                CurrentCheckinTypeId = PageParameter( "CheckinConfigId" ).AsIntegerOrNull();
                CurrentGroupTypeIds = ( PageParameter( "GroupTypeIds" ) ?? "" )
                    .Split( new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries )
                    .ToList()
                    .Select( s => s.AsInteger() )
                    .ToList();

                // If Kiosk and GroupTypes were passed, but not a checkin type, try to calculate it from the group types.
                if ( CurrentKioskId.HasValue && CurrentGroupTypeIds.Any() && !CurrentCheckinTypeId.HasValue )
                {
                    if ( !CurrentCheckinTypeId.HasValue )
                    {
                        foreach ( int groupTypeId in CurrentGroupTypeIds )
                        {
                            var checkinType = GetCheckinType( groupTypeId );
                            if ( checkinType != null )
                            {
                                CurrentCheckinTypeId = checkinType.Id;
                                break;
                            }
                        }
                    }
                }

                // If valid parameters were used, set state and navigate to welcome page
                if ( CurrentKioskId.HasValue && CurrentGroupTypeIds.Any() && CurrentCheckinTypeId.HasValue && !themeRedirect )
                {
                    // Save the check-in state
                    SaveState();

                    // Navigate to the check-in home (welcome) page
                    NavigateToNextPage();
                }
                else
                {
                    bool enableLocationSharing = GetAttributeValue( "EnableLocationSharing" ).AsBoolean();

                    // Inject script used for geo location determiniation
                    if ( enableLocationSharing )
                    {
                        lbRetry.Visible = true;
                        AddGeoLocationScript();
                    }
                    else
                    {
                        pnlManualConfig.Visible = true;
                        lbOk.Visible = true;
                        AttemptKioskMatchByIpOrName();
                    }

                    if ( !themeRedirect )
                    {
                        string script = string.Format( @"
                    <script>
                        $(document).ready(function (e) {{
                            if (localStorage) {{
                                if (localStorage.checkInKiosk) {{
                                    $('[id$=""hfKiosk""]').val(localStorage.checkInKiosk);
                                    if (localStorage.theme) {{
                                        $('[id$=""hfTheme""]').val(localStorage.theme);
                                    }}
                                    if (localStorage.checkInType) {{
                                        $('[id$=""hfCheckinType""]').val(localStorage.checkInType);
                                    }}
                                    if (localStorage.checkInGroupTypes) {{
                                        $('[id$=""hfGroupTypes""]').val(localStorage.checkInGroupTypes);
                                    }}
                                    {0};
                                }}
                            }}
                        }});
                    </script>
                ", this.Page.ClientScript.GetPostBackEventReference( lbRefresh, "" ) );
                        phScript.Controls.Add( new LiteralControl( script ) );
                    }

                    ddlTheme.Items.Clear();
                    DirectoryInfo di = new DirectoryInfo( this.Page.Request.MapPath( ResolveRockUrl( "~~" ) ) );
                    foreach ( var themeDir in di.Parent.EnumerateDirectories().OrderBy( a => a.Name ) )
                    {
                        ddlTheme.Items.Add( new ListItem( themeDir.Name, themeDir.Name.ToLower() ) );
                    }

                    if ( !string.IsNullOrWhiteSpace( CurrentTheme ) )
                    {
                        ddlTheme.SetValue( CurrentTheme );
                    }
                    else
                    {
                        ddlTheme.SetValue( RockPage.Site.Theme.ToLower() );
                    }

                    Guid kioskDeviceType = Rock.SystemGuid.DefinedValue.DEVICE_TYPE_CHECKIN_KIOSK.AsGuid();
                    ddlKiosk.Items.Clear();
                    using ( var rockContext = new RockContext() )
                    {
                        ddlKiosk.DataSource = new DeviceService( rockContext )
                            .Queryable().AsNoTracking()
                            .Where( d => d.DeviceType.Guid.Equals( kioskDeviceType ) )
                            .OrderBy( d => d.Name )
                            .Select( d => new
                            {
                                d.Id,
                                d.Name
                            } )
                            .ToList();
                    }
                    ddlKiosk.DataBind();
                    ddlKiosk.Items.Insert( 0, new ListItem( None.Text, None.IdValue ) );

                    if ( CurrentKioskId.HasValue )
                    {
                        ListItem item = ddlKiosk.Items.FindByValue( CurrentKioskId.Value.ToString() );
                        if ( item != null )
                        {
                            item.Selected = true;
                            BindCheckinTypes();
                            BindGroupTypes();
                        }
                    }
                }
            }
            else
            {
                phScript.Controls.Clear();
            }
        }

        /// <summary>
        /// Attempts to match a known kiosk based on the IP address of the client.
        /// </summary>
        private void AttemptKioskMatchByIpOrName()
        {
            // try to find matching kiosk by REMOTE_ADDR (ip/name).
            var checkInDeviceTypeId = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.DEVICE_TYPE_CHECKIN_KIOSK ).Id;
            using ( var rockContext = new RockContext() )
            {
                bool enableReverseLookup = GetAttributeValue( "EnableReverseLookup" ).AsBoolean( false );
                var device = new DeviceService( rockContext ).GetByIPAddress( Rock.Web.UI.RockPage.GetClientIpAddress(), checkInDeviceTypeId, !enableReverseLookup );
                if ( device != null )
                {
                    ClearMobileCookie();
                    CurrentKioskId = device.Id;
                    CurrentGroupTypeIds = GetAllKiosksGroupTypes( device, rockContext ); ;

                    if ( !CurrentCheckinTypeId.HasValue )
                    {
                        foreach ( int groupTypeId in CurrentGroupTypeIds )
                        {
                            var checkinType = GetCheckinType( groupTypeId );
                            if ( checkinType != null )
                            {
                                CurrentCheckinTypeId = checkinType.Id;
                                break;
                            }
                        }
                    }

                    CurrentCheckInState = null;
                    CurrentWorkflow = null;
                    SaveState();
                    NavigateToNextPage();
                }
            }
        }

        /// <summary>
        /// Adds GeoLocation script and calls its init() to get client's latitude/longitude before firing
        /// the server side lbCheckGeoLocation_Click click event. Puts the two values into the two corresponding
        /// hidden varialbles, hfLatitude and hfLongitude.
        /// </summary>
        private void AddGeoLocationScript()
        {
            string geoScript = string.Format( @"
    <script>
        $(document).ready(function (e) {{
            tryGeoLocation();

            function tryGeoLocation() {{
                if ( geo_position_js.init() ) {{
                    geo_position_js.getCurrentPosition(success_callback, error_callback, {{ enableHighAccuracy: true }});
                }}
                else {{
                    $(""div.checkin-header h1"").html( ""We're Sorry!"" );
                    $(""div.checkin-header h1"").after( ""<p>We don't support that kind of device yet. Please Check in using the on-site kiosks.</p>"" );
                    alert(""We don't support that kind of device yet. Please Check in using the on-site kiosks."");
                }}
            }}

            function success_callback( p ) {{
                var latitude = p.coords.latitude.toFixed(4);
                var longitude = p.coords.longitude.toFixed(4);
                $(""input[id$='hfLatitude']"").val( latitude );
                $(""input[id$='hfLongitude']"").val( longitude );
                $(""div.checkin-header h1"").html( 'Checking Your Location...' );
                $(""div.checkin-header"").append( ""<p class='text-muted'>"" + latitude + "" "" + longitude + ""</p>"" );
                // now perform a postback to fire the check geo location
                {0};
            }}

            function error_callback( p ) {{
                // TODO: decide what to do in this situation...
                alert( 'error=' + p.message );
            }}
        }});
    </script>
", this.Page.ClientScript.GetPostBackEventReference( lbCheckGeoLocation, "" ) );
            phScript.Controls.Add( new LiteralControl( geoScript ) );
        }

        /// <summary>
        /// Used by the local storage script to rebind the group types if they were previously
        /// saved via local storage.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void lbRefresh_Click( object sender, EventArgs e )
        {
            if ( !string.IsNullOrWhiteSpace( hfTheme.Value ) &&
                !hfTheme.Value.Equals( ddlTheme.SelectedValue, StringComparison.OrdinalIgnoreCase ) &&
                Directory.Exists( Path.Combine( this.Page.Request.MapPath( ResolveRockUrl( "~~" ) ), hfTheme.Value ) ) )
            {
                CurrentTheme = hfTheme.Value;
                RedirectToNewTheme( hfTheme.Value );
            }
            else
            {
                ddlKiosk.SetValue( hfKiosk.Value );
                BindCheckinTypes( hfCheckinType.Value.AsIntegerOrNull() );
                BindGroupTypes( hfGroupTypes.Value );
            }
        }

        #region GeoLocation related

        /// <summary>
        /// Handles attempting to find a registered Device kiosk by it's latitude and longitude.
        /// This event method is called automatically when the GeoLocation script get's the client's location.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void lbCheckGeoLocation_Click( object sender, EventArgs e )
        {
            var lat = hfLatitude.Value;
            var lon = hfLongitude.Value;
            Device kiosk = null;

            if ( !string.IsNullOrEmpty( lat ) && !string.IsNullOrEmpty( lon ) )
            {
                kiosk = GetCurrentKioskByGeoFencing( lat, lon );
            }

            if ( kiosk != null )
            {
                SetDeviceIdCookie( kiosk );

                CurrentKioskId = kiosk.Id;
                using ( var rockContext = new RockContext() )
                {
                    CurrentGroupTypeIds = GetAllKiosksGroupTypes( kiosk, rockContext ); ;
                }

                if ( !CurrentCheckinTypeId.HasValue )
                {
                    foreach ( int groupTypeId in CurrentGroupTypeIds )
                    {
                        var checkinType = GetCheckinType( groupTypeId );
                        if ( checkinType != null )
                        {
                            CurrentCheckinTypeId = checkinType.Id;
                            break;
                        }
                    }
                }

                CurrentCheckInState = null;
                CurrentWorkflow = null;
                SaveState();

                NavigateToNextPage();
            }
            else
            {
                TooFar();
            }
        }

        /// <summary>
        /// Sets the "DeviceId" cookie to expire after TimeToCacheKioskGeoLocation minutes
        /// if IsMobile is set.
        /// </summary>
        /// <param name="kiosk"></param>
        private void SetDeviceIdCookie( Device kiosk )
        {
            // set an expiration cookie for these coordinates.
            double timeCacheMinutes = double.Parse( GetAttributeValue( "TimetoCacheKioskGeoLocation" ) ?? "0" );

            HttpCookie deviceCookie = Request.Cookies[CheckInCookie.DEVICEID];
            if ( deviceCookie == null )
            {
                deviceCookie = new HttpCookie( CheckInCookie.DEVICEID, kiosk.Id.ToString() );
            }

            deviceCookie.Expires = ( timeCacheMinutes == 0 ) ? DateTime.MaxValue : RockDateTime.Now.AddMinutes( timeCacheMinutes );
            Response.Cookies.Set( deviceCookie );

            HttpCookie isMobileCookie = new HttpCookie( CheckInCookie.ISMOBILE, "true" );
            Response.Cookies.Set( isMobileCookie );
        }

        /// <summary>
        /// Clears the flag cookie that indicates this is a "mobile" device kiosk.
        /// </summary>
        private void ClearMobileCookie()
        {
            HttpCookie isMobileCookie = new HttpCookie( CheckInCookie.ISMOBILE );
            isMobileCookie.Expires = RockDateTime.Now.AddDays( -1d );
            Response.Cookies.Set( isMobileCookie );
        }

        /// <summary>
        /// Returns a list of IDs that are the GroupTypes the kiosk is responsible for.
        /// </summary>
        /// <param name="kiosk"></param>
        /// <returns></returns>
        private List<int> GetAllKiosksGroupTypes( Device kiosk, RockContext rockContext )
        {
            var groupTypes = GetDeviceGroupTypes( kiosk.Id, rockContext );
            var groupTypeIds = groupTypes.Select( gt => gt.Id ).ToList();
            return groupTypeIds;
        }

        /// <summary>
        /// Display a "too far" message.
        /// </summary>
        private void TooFar()
        {
            bool allowManualSetup = GetAttributeValue( "AllowManualSetup" ).AsBoolean( true );

            if ( allowManualSetup )
            {
                pnlManualConfig.Visible = true;
                lbOk.Visible = true;
                maWarning.Show( "We could not automatically determine your configuration.", ModalAlertType.Information );
            }
            else
            {
                maWarning.Show( "You are too far. Try again later.", ModalAlertType.Alert );
            }
        }

        protected void lbRetry_Click( object sender, EventArgs e )
        {
            // TODO
        }

        #endregion

        #region Manually Setting Kiosks related

        protected void ddlTheme_SelectedIndexChanged( object sender, EventArgs e )
        {
            CurrentTheme = ddlTheme.SelectedValue;
            RedirectToNewTheme( ddlTheme.SelectedValue );
        }

        protected void ddlKiosk_SelectedIndexChanged( object sender, EventArgs e )
        {
            BindCheckinTypes();
            BindGroupTypes();
        }

        protected void ddlCheckinType_SelectedIndexChanged( object sender, EventArgs e )
        {
            BindGroupTypes();
        }

        protected void lbOk_Click( object sender, EventArgs e )
        {
            if ( ddlKiosk.SelectedValue == None.IdValue )
            {
                maWarning.Show( "A Kiosk Device needs to be selected!", ModalAlertType.Warning );
                return;
            }

            var groupTypeIds = new List<int>();
            foreach ( ListItem item in cblPrimaryGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( Int32.Parse( item.Value ) );
                }
            }
            foreach ( ListItem item in cblAlternateGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( Int32.Parse( item.Value ) );
                }
            }

            ClearMobileCookie();
            CurrentTheme = ddlTheme.SelectedValue;
            CurrentKioskId = ddlKiosk.SelectedValueAsInt();
            CurrentCheckinTypeId = ddlCheckinType.SelectedValueAsInt();
            CurrentGroupTypeIds = groupTypeIds;
            CurrentCheckInState = null;
            CurrentWorkflow = null;
            SaveState();

            NavigateToNextPage();
        }

        /// <summary>
        /// Gets the device group types.
        /// </summary>
        /// <param name="deviceId">The device identifier.</param>
        /// <returns></returns>
        private List<GroupType> GetDeviceGroupTypes( int deviceId, RockContext rockContext )
        {
            var groupTypes = new Dictionary<int, GroupType>();

            var locationService = new LocationService( rockContext );

            // Get all locations (and their children) associated with device
            var locationIds = locationService
                .GetByDevice( deviceId, true )
                .Select( l => l.Id )
                .ToList();

            // Requery using EF
            foreach ( var groupType in locationService
                .Queryable().AsNoTracking()
                .Where( l => locationIds.Contains( l.Id ) )
                .SelectMany( l => l.GroupLocations )
                .Where( gl => gl.Group.GroupType.TakesAttendance )
                .Select( gl => gl.Group.GroupType )
                .ToList() )
            {
                groupTypes.AddOrIgnore( groupType.Id, groupType );
            }

            return groupTypes
                .Select( g => g.Value )
                .OrderBy( g => g.Order )
                .ToList();
        }

        private void BindCheckinTypes()
        {
            BindCheckinTypes( ddlCheckinType.SelectedValueAsInt() );
        }

        private void BindCheckinTypes( int? selectedValue )
        {
            ddlCheckinType.Items.Clear();

            if ( ddlKiosk.SelectedValue != None.IdValue )
            {
                using ( var rockContext = new RockContext() )
                {
                    var kioskCheckinTypes = new List<GroupType>();

                    var kioskGroupTypes = GetDeviceGroupTypes( ddlKiosk.SelectedValueAsInt() ?? 0, rockContext );
                    var kioskGroupTypeIds = kioskGroupTypes.Select( t => t.Id ).ToList();

                    var groupTypeService = new GroupTypeService( rockContext );

                    Guid templateTypeGuid = Rock.SystemGuid.DefinedValue.GROUPTYPE_PURPOSE_CHECKIN_TEMPLATE.AsGuid();
                    ddlCheckinType.DataSource = groupTypeService
                        .Queryable().AsNoTracking()
                        .Where( t =>
                            t.GroupTypePurposeValue != null &&
                            t.GroupTypePurposeValue.Guid == templateTypeGuid )
                        .OrderBy( t => t.Name )
                        .Select( t => new
                        {
                            t.Name,
                            t.Id
                        } )
                        .ToList();
                    ddlCheckinType.DataBind();
                }

                if ( selectedValue.HasValue )
                {
                    ddlCheckinType.SetValue( selectedValue );
                }
                else
                {
                    if ( CurrentCheckinTypeId.HasValue )
                    {
                        ddlCheckinType.SetValue( CurrentCheckinTypeId );
                    }
                    else
                    {
                        var groupType = GroupTypeCache.Read( Rock.SystemGuid.GroupType.GROUPTYPE_WEEKLY_SERVICE_CHECKIN_AREA.AsGuid() );
                        if ( groupType != null  )
                        {
                            ddlCheckinType.SetValue( groupType.Id );
                        }
                    }
                }
            }
        }

        private GroupTypeCache GetCheckinType( int? groupTypeId )
        {
            Guid templateTypeGuid = Rock.SystemGuid.DefinedValue.GROUPTYPE_PURPOSE_CHECKIN_TEMPLATE.AsGuid();
            var templateType = DefinedValueCache.Read( templateTypeGuid );
            if ( templateType != null )
            {
                return GetCheckinType( GroupTypeCache.Read( groupTypeId.Value ), templateType.Id );
            }

            return null;
        }

        private GroupTypeCache GetCheckinType( GroupTypeCache groupType, int templateTypeId, List<int> recursionControl = null )
        {
            if ( groupType != null )
            {
                recursionControl = recursionControl ?? new List<int>();
                if ( !recursionControl.Contains( groupType.Id ) )
                {
                    recursionControl.Add( groupType.Id );
                    if ( groupType.GroupTypePurposeValueId.HasValue && groupType.GroupTypePurposeValueId == templateTypeId )
                    {
                        return groupType;
                    }

                    foreach ( var parentGroupType in groupType.ParentGroupTypes )
                    {
                        var checkinType = GetCheckinType( parentGroupType, templateTypeId, recursionControl );
                        if ( checkinType != null )
                        {
                            return checkinType;
                        }
                    }
                }
            }

            return null;
        }


        private List<GroupTypeCache> GetDescendentGroupTypes( GroupTypeCache groupType, List<int> recursionControl = null )
        {
            var results = new List<GroupTypeCache>();

            if ( groupType != null )
            {
                recursionControl = recursionControl ?? new List<int>();
                if ( !recursionControl.Contains( groupType.Id ) )
                {
                    recursionControl.Add( groupType.Id );
                    results.Add( groupType );

                    foreach ( var childGroupType in groupType.ChildGroupTypes )
                    {
                        var childResults = GetDescendentGroupTypes( childGroupType, recursionControl );
                        childResults.ForEach( c => results.Add( c ) );
                    }
                }
            }

            return results;
        }

        private void BindGroupTypes()
        {
            var groupTypeIds = new List<string>();
            foreach ( ListItem item in cblPrimaryGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( item.Value );
                }
            }
            foreach ( ListItem item in cblAlternateGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( item.Value );
                }
            }

            BindGroupTypes( groupTypeIds.AsDelimited(",") );
        }

        private void BindGroupTypes( string selectedValues )
        {
            var selectedItems = selectedValues.Split( ',' );

            cblPrimaryGroupTypes.Items.Clear();
            cblAlternateGroupTypes.Items.Clear();

            if ( ddlKiosk.SelectedValue != None.IdValue ) 
            {
                using ( var rockContext = new RockContext() )
                {
                    var deviceGroupTypes = GetDeviceGroupTypes( ddlKiosk.SelectedValueAsInt() ?? 0, rockContext );

                    var primaryGroupTypeIds = GetDescendentGroupTypes( GroupTypeCache.Read( ddlCheckinType.SelectedValueAsInt() ?? 0 ) ).Select( t => t.Id ).ToList();

                    cblPrimaryGroupTypes.DataSource = deviceGroupTypes.Where( t => primaryGroupTypeIds.Contains( t.Id ) ).ToList();
                    cblPrimaryGroupTypes.DataBind();
                    cblPrimaryGroupTypes.Visible = cblPrimaryGroupTypes.Items.Count > 0;

                    cblAlternateGroupTypes.DataSource = deviceGroupTypes.Where( t => !primaryGroupTypeIds.Contains( t.Id ) ).ToList();
                    cblAlternateGroupTypes.DataBind();
                    cblAlternateGroupTypes.Visible = cblPrimaryGroupTypes.Items.Count > 0;
                }

                if ( selectedValues != string.Empty )
                {
                    foreach ( string id in selectedValues.Split( ',' ) )
                    {
                        ListItem item = cblPrimaryGroupTypes.Items.FindByValue( id );
                        if ( item != null )
                        {
                            item.Selected = true;
                        }

                        item = cblAlternateGroupTypes.Items.FindByValue( id );
                        if ( item != null )
                        {
                            item.Selected = true;
                        }
                    }
                }
                else
                {
                    if ( CurrentGroupTypeIds != null )
                    {
                        foreach ( int id in CurrentGroupTypeIds )
                        {
                            ListItem item = cblPrimaryGroupTypes.Items.FindByValue( id.ToString() );
                            if ( item != null )
                            {
                                item.Selected = true;
                            }

                            item = cblAlternateGroupTypes.Items.FindByValue( id.ToString() );
                            if ( item != null )
                            {
                                item.Selected = true;
                            }

                        }
                    }
                }
            }
            else
            {
                cblPrimaryGroupTypes.Visible = false;
                cblAlternateGroupTypes.Visible = false;
            }
        }

        /// <summary>
        /// Returns a kiosk based on finding a geo location match for the given latitude and longitude.
        /// </summary>
        /// <param name="sLatitude">latitude as string</param>
        /// <param name="sLongitude">longitude as string</param>
        /// <returns></returns>
        public static Device GetCurrentKioskByGeoFencing( string sLatitude, string sLongitude )
        {
            double latitude = double.Parse( sLatitude );
            double longitude = double.Parse( sLongitude );
            var checkInDeviceTypeId = DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.DEVICE_TYPE_CHECKIN_KIOSK ).Id;

            // We need to use the DeviceService until we can get the GeoFence to JSON Serialize/Deserialize.
            using ( var rockContext = new RockContext() )
            {
                Device kiosk = new DeviceService( rockContext ).GetByGeocode( latitude, longitude, checkInDeviceTypeId );
                return kiosk;
            }
        }

        private void RedirectToNewTheme( string theme )
        {
            var pageRef = RockPage.PageReference;
            pageRef.QueryString = new System.Collections.Specialized.NameValueCollection();
            pageRef.Parameters = new Dictionary<string, string>();
            pageRef.Parameters.Add( "theme", theme );
            pageRef.Parameters.Add( "KioskId", ddlKiosk.SelectedValue );
            pageRef.Parameters.Add( "CheckinConfigId", ddlCheckinType.SelectedValue );

            var groupTypeIds = new List<string>();
            foreach ( ListItem item in cblPrimaryGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( item.Value );
                }
            }
            foreach ( ListItem item in cblAlternateGroupTypes.Items )
            {
                if ( item.Selected )
                {
                    groupTypeIds.Add( item.Value );
                }
            }
            pageRef.Parameters.Add( "GroupTypeIds", groupTypeIds.AsDelimited( "," ) );
            pageRef.Parameters.Add( "ThemeRedirect", "True" );

            Response.Redirect( pageRef.BuildUrl(), false );
        }

        #endregion

    }
}