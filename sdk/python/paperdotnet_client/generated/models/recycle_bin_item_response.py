from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .item_response import ItemResponse

@dataclass
class RecycleBinItemResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The deletedAt property
    deleted_at: Optional[datetime.datetime] = None
    # The deletedBy property
    deleted_by: Optional[UUID] = None
    # A list item as returned by the API. `fields` contains `title` and all field values.
    item: Optional[ItemResponse] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RecycleBinItemResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RecycleBinItemResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RecycleBinItemResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .item_response import ItemResponse

        from .item_response import ItemResponse

        fields: dict[str, Callable[[Any], None]] = {
            "deletedAt": lambda n : setattr(self, 'deleted_at', n.get_datetime_value()),
            "deletedBy": lambda n : setattr(self, 'deleted_by', n.get_uuid_value()),
            "item": lambda n : setattr(self, 'item', n.get_object_value(ItemResponse)),
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
        writer.write_datetime_value("deletedAt", self.deleted_at)
        writer.write_uuid_value("deletedBy", self.deleted_by)
        writer.write_object_value("item", self.item)
        writer.write_additional_data_value(self.additional_data)
    

