from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .settings_response_channels import SettingsResponse_channels

@dataclass
class SettingsResponse(AdditionalDataHolder, Parsable):
    """
    Settings; string? SettingsResponse.WebhookSecret is only returned when a secret was created (shown once).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The channels property
    channels: Optional[SettingsResponse_channels] = None
    # The digestHour property
    digest_hour: Optional[int] = None
    # The ETag for `If-Match` on changes (the same as the `ETag` header).
    odata_etag: Optional[str] = None
    # The quietHoursEnd property
    quiet_hours_end: Optional[datetime.time] = None
    # The quietHoursStart property
    quiet_hours_start: Optional[datetime.time] = None
    # The webhookSecret property
    webhook_secret: Optional[str] = None
    # The webhookUrl property
    webhook_url: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SettingsResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SettingsResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SettingsResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .settings_response_channels import SettingsResponse_channels

        from .settings_response_channels import SettingsResponse_channels

        fields: dict[str, Callable[[Any], None]] = {
            "channels": lambda n : setattr(self, 'channels', n.get_object_value(SettingsResponse_channels)),
            "digestHour": lambda n : setattr(self, 'digest_hour', n.get_int_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
            "quietHoursEnd": lambda n : setattr(self, 'quiet_hours_end', n.get_time_value()),
            "quietHoursStart": lambda n : setattr(self, 'quiet_hours_start', n.get_time_value()),
            "webhookSecret": lambda n : setattr(self, 'webhook_secret', n.get_str_value()),
            "webhookUrl": lambda n : setattr(self, 'webhook_url', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_object_value("channels", self.channels)
        writer.write_int_value("digestHour", self.digest_hour)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_time_value("quietHoursEnd", self.quiet_hours_end)
        writer.write_time_value("quietHoursStart", self.quiet_hours_start)
        writer.write_str_value("webhookSecret", self.webhook_secret)
        writer.write_str_value("webhookUrl", self.webhook_url)
        writer.write_additional_data_value(self.additional_data)
    

