from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .item_response import ItemResponse

@dataclass
class ItemPage(AdditionalDataHolder, Parsable):
    """
    A page of items in Graph/OData shape.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The OdataCount property
    odata_count: Optional[int] = None
    # The OdataNextLink property
    odata_next_link: Optional[str] = None
    # The value property
    value: Optional[list[ItemResponse]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ItemPage:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ItemPage
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ItemPage()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .item_response import ItemResponse

        from .item_response import ItemResponse

        fields: dict[str, Callable[[Any], None]] = {
            "@odata.count": lambda n : setattr(self, 'odata_count', n.get_int_value()),
            "@odata.nextLink": lambda n : setattr(self, 'odata_next_link', n.get_str_value()),
            "value": lambda n : setattr(self, 'value', n.get_collection_of_object_values(ItemResponse)),
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
        writer.write_int_value("@odata.count", self.odata_count)
        writer.write_str_value("@odata.nextLink", self.odata_next_link)
        writer.write_collection_of_object_values("value", self.value)
        writer.write_additional_data_value(self.additional_data)
    

