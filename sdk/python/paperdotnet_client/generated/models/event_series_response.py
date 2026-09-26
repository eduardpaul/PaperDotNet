from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .occurrence_override_response import OccurrenceOverrideResponse

@dataclass
class EventSeriesResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The cancelled property
    cancelled: Optional[list[datetime.datetime]] = None
    # The moved property
    moved: Optional[list[OccurrenceOverrideResponse]] = None
    # The rule property
    rule: Optional[str] = None
    # The timeZone property
    time_zone: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> EventSeriesResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: EventSeriesResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return EventSeriesResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .occurrence_override_response import OccurrenceOverrideResponse

        from .occurrence_override_response import OccurrenceOverrideResponse

        fields: dict[str, Callable[[Any], None]] = {
            "cancelled": lambda n : setattr(self, 'cancelled', n.get_collection_of_primitive_values(datetime.datetime)),
            "moved": lambda n : setattr(self, 'moved', n.get_collection_of_object_values(OccurrenceOverrideResponse)),
            "rule": lambda n : setattr(self, 'rule', n.get_str_value()),
            "timeZone": lambda n : setattr(self, 'time_zone', n.get_str_value()),
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
        writer.write_collection_of_primitive_values("cancelled", self.cancelled)
        writer.write_collection_of_object_values("moved", self.moved)
        writer.write_str_value("rule", self.rule)
        writer.write_str_value("timeZone", self.time_zone)
        writer.write_additional_data_value(self.additional_data)
    

