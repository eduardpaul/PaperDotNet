from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .batch_request_item_headers import BatchRequestItem_headers
    from .json_element import JsonElement

@dataclass
class BatchRequestItem(AdditionalDataHolder, Parsable):
    """
    One request of a batch. `url` is relative to `/v1.0` (or starts with it).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The body property
    body: Optional[JsonElement] = None
    # The dependsOn property
    depends_on: Optional[list[str]] = None
    # The headers property
    headers: Optional[BatchRequestItem_headers] = None
    # The id property
    id: Optional[str] = None
    # The method property
    method: Optional[str] = None
    # The url property
    url: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> BatchRequestItem:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: BatchRequestItem
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return BatchRequestItem()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .batch_request_item_headers import BatchRequestItem_headers
        from .json_element import JsonElement

        from .batch_request_item_headers import BatchRequestItem_headers
        from .json_element import JsonElement

        fields: dict[str, Callable[[Any], None]] = {
            "body": lambda n : setattr(self, 'body', n.get_object_value(JsonElement)),
            "dependsOn": lambda n : setattr(self, 'depends_on', n.get_collection_of_primitive_values(str)),
            "headers": lambda n : setattr(self, 'headers', n.get_object_value(BatchRequestItem_headers)),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "method": lambda n : setattr(self, 'method', n.get_str_value()),
            "url": lambda n : setattr(self, 'url', n.get_str_value()),
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
        writer.write_object_value("body", self.body)
        writer.write_collection_of_primitive_values("dependsOn", self.depends_on)
        writer.write_object_value("headers", self.headers)
        writer.write_str_value("id", self.id)
        writer.write_str_value("method", self.method)
        writer.write_str_value("url", self.url)
        writer.write_additional_data_value(self.additional_data)
    

