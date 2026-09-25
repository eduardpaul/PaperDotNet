from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .settings_request_channels import SettingsRequest_channels

@dataclass
class SettingsRequest(AdditionalDataHolder, Parsable):
    """
    Replaces the settings. Channels not listed use the defaults (in-app and webhook on).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The channels property
    channels: Optional[SettingsRequest_channels] = None
    # The digestHour property
    digest_hour: Optional[int] = None
    # The quietHoursEnd property
    quiet_hours_end: Optional[datetime.time] = None
    # The quietHoursStart property
    quiet_hours_start: Optional[datetime.time] = None
    # The webhookUrl property
    webhook_url: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SettingsRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SettingsRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SettingsRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .settings_request_channels import SettingsRequest_channels

        from .settings_request_channels import SettingsRequest_channels

        fields: dict[str, Callable[[Any], None]] = {
            "channels": lambda n : setattr(self, 'channels', n.get_object_value(SettingsRequest_channels)),
            "digestHour": lambda n : setattr(self, 'digest_hour', n.get_int_value()),
            "quietHoursEnd": lambda n : setattr(self, 'quiet_hours_end', n.get_time_value()),
            "quietHoursStart": lambda n : setattr(self, 'quiet_hours_start', n.get_time_value()),
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
        writer.write_time_value("quietHoursEnd", self.quiet_hours_end)
        writer.write_time_value("quietHoursStart", self.quiet_hours_start)
        writer.write_str_value("webhookUrl", self.webhook_url)
        writer.write_additional_data_value(self.additional_data)
    

